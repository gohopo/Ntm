using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ntm.Gateway.Rewrite;

/// <summary>
/// 一个 Channel 的改写规则，解析成快照里用的不可变形式（Q144–Q151）。
/// 请求体规则先跑，网关自己的改写（模型名、include_usage）后跑，见 ADR-0002。
/// </summary>
public sealed class RequestRewriter
{
    /// <summary>
    /// 改不了的请求头（Q145）：认证类改了凭证就失效；后面几个由 HTTP 栈自己管，手动改会让连接错乱。
    /// </summary>
    public static readonly FrozenSet<string> ProtectedHeaders = new[]
    {
        "authorization", "x-api-key", "api-key", "proxy-authorization", "cookie",
        "host", "content-length", "content-type", "transfer-encoding", "connection",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 请求体里改不了的顶层字段（Q145）：model 决定计价和冷却的键，stream 决定我们按什么方式读响应。
    /// </summary>
    public static readonly FrozenSet<string> ProtectedBodyFields = new[] { "model", "stream" }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // 中文、emoji 原样写，不转成 \uXXXX：上游能读，日志和调试原文也好看。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RequestRewriter Empty { get; } = new([], []);

    private readonly IReadOnlyList<HeaderRule> _headers;
    private readonly IReadOnlyList<BodyRule> _body;

    private RequestRewriter(IReadOnlyList<HeaderRule> headers, IReadOnlyList<BodyRule> body)
    {
        _headers = headers;
        _body = body;
        CacheBreakpointsWritten = body
            .Where(rule => rule.Operation != RewriteOperation.Remove)
            .Sum(rule => (rule.Path is ["cache_control"] ? 1 : 0) + CountCacheControl(rule.Value));
    }

    public bool HasBodyRules => _body.Count > 0;

    public bool HasHeaderRules => _headers.Count > 0;

    /// <summary>
    /// 规则会往请求体里写几个缓存断点（设置或默认；去掉的规则不算）：顶层 <c>cache_control</c> 算一个，
    /// Anthropic 把它当自动缓存（Q307）；规则值里每出现一个 <c>cache_control</c> 也算一个（Q316）。
    /// 网关自己放断点时要让出这么多。
    /// </summary>
    public int CacheBreakpointsWritten { get; }

    /// <summary>有规则（设置、默认或去掉）碰请求体顶层的这个字段。</summary>
    public bool TouchesTopLevel(string field) => _body.Any(rule => rule.Path.Length > 0 && rule.Path[0] == field);

    private static int CountCacheControl(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(p => (p.Key == "cache_control" ? 1 : 0) + (p.Key == "cache_control" ? 0 : CountCacheControl(p.Value))),
        JsonArray array => array.Sum(CountCacheControl),
        _ => 0,
    };

    /// <summary>
    /// 从存着的规则建出改写器。存进库之前已经校验过；这里遇到坏规则（手改了库）就跳过那一条。
    /// </summary>
    public static RequestRewriter From(IReadOnlyList<ChannelRewriteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
        {
            return Empty;
        }

        List<HeaderRule> headers = [];
        List<BodyRule> body = [];
        foreach (ChannelRewriteRule rule in rules)
        {
            if (Validate(rule) is not null)
            {
                continue;
            }

            if (rule.Target == RewriteTarget.Header)
            {
                headers.Add(new HeaderRule(rule.Operation, rule.Path.Trim(), rule.Value ?? ""));
            }
            else
            {
                JsonNode? value = rule.Operation == RewriteOperation.Remove ? null : JsonNode.Parse(rule.Value!);
                body.Add(new BodyRule(rule.Operation, rule.Path.Trim().Split('.'), value));
            }
        }

        return headers.Count == 0 && body.Count == 0 ? Empty : new RequestRewriter(headers, body);
    }

    /// <summary>
    /// 校验一条规则。返回 null 表示没问题，否则返回 (字段, 错误) —— 字段是 <c>path</c> 或 <c>value</c>。
    /// </summary>
    public static (string Field, string Message)? Validate(ChannelRewriteRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!Enum.IsDefined(rule.Target))
        {
            return ("target", "改写位置只能是请求头或请求体。");
        }

        if (!Enum.IsDefined(rule.Operation))
        {
            return ("operation", "操作只能是设置、删除或补充。");
        }

        string path = rule.Path?.Trim() ?? "";
        if (path.Length == 0)
        {
            return ("path", rule.Target == RewriteTarget.Header ? "请填写请求头名。" : "请填写字段路径。");
        }

        if (path.Length > ChannelRewriteRule.MaxPathLength)
        {
            return ("path", $"不能超过 {ChannelRewriteRule.MaxPathLength} 个字符。");
        }

        if (rule.Target == RewriteTarget.Header)
        {
            if (!IsHeaderToken(path))
            {
                return ("path", "请求头名只能用字母、数字和 !#$%&'*+-.^_`|~ 这些字符。");
            }

            if (ProtectedHeaders.Contains(path))
            {
                return ("path", $"{path} 由网关管理，不能改写。");
            }

            if (rule.Operation != RewriteOperation.Remove)
            {
                string value = rule.Value ?? "";
                if (value.Length > ChannelRewriteRule.MaxValueLength)
                {
                    return ("value", $"不能超过 {ChannelRewriteRule.MaxValueLength} 个字符。");
                }

                if (value.Any(c => c is '\r' or '\n' or '\0'))
                {
                    return ("value", "请求头的值不能换行。");
                }
            }

            return null;
        }

        string[] segments = path.Split('.');
        if (segments.Any(s => s.Length == 0))
        {
            return ("path", "路径用点号分隔，每一段都不能为空，例如 thinking.type。");
        }

        if (ProtectedBodyFields.Contains(segments[0]))
        {
            return ("path", $"{segments[0]} 由网关管理，不能改写。");
        }

        if (rule.Operation == RewriteOperation.Remove)
        {
            return null;
        }

        string? literal = rule.Value;
        if (string.IsNullOrWhiteSpace(literal))
        {
            return ("value", "请填写一个 JSON 值，例如 \"disabled\"、0.7、true、null 或 {\"a\":1}。");
        }

        if (literal.Length > ChannelRewriteRule.MaxValueLength)
        {
            return ("value", $"不能超过 {ChannelRewriteRule.MaxValueLength} 个字符。");
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(literal);
        }
        catch (JsonException)
        {
            return ("value", "不是合法的 JSON 值。字符串要带双引号，例如 \"disabled\"。");
        }

        return null;
    }

    /// <summary>
    /// 把请求体规则套到一份 JSON 请求体上。没有请求体规则、或者请求体不是 JSON 对象时原样返回。
    /// </summary>
    public byte[] ApplyToBody(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_body.Count == 0)
        {
            return body;
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed)
            {
                return body;
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return body;
        }

        foreach (BodyRule rule in _body)
        {
            Apply(root, rule);
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, WriteOptions);
    }

    /// <summary>把请求头规则套到发往上游的请求上。认证头已经由调用方写好，这里不会碰到它们。</summary>
    public void ApplyToHeaders(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (HeaderRule rule in _headers)
        {
            switch (rule.Operation)
            {
                case RewriteOperation.Set:
                    headers.Remove(rule.Name);
                    headers.TryAddWithoutValidation(rule.Name, rule.Value);
                    break;
                case RewriteOperation.Remove:
                    headers.Remove(rule.Name);
                    break;
                case RewriteOperation.Default when !headers.Contains(rule.Name):
                    headers.TryAddWithoutValidation(rule.Name, rule.Value);
                    break;
            }
        }
    }

    private static void Apply(JsonObject root, BodyRule rule)
    {
        JsonObject parent = root;
        for (int i = 0; i < rule.Path.Length - 1; i++)
        {
            string segment = rule.Path[i];
            JsonNode? next = parent[segment];
            if (next is JsonObject child)
            {
                parent = child;
                continue;
            }

            // 中间层不存在：删除不用做什么；设置和补充把它建出来（Q146）。
            // 中间层存在但不是对象（字符串、数组…）：不动它，宁可这条规则不生效也不毁掉客户端给的值。
            if (next is not null || rule.Operation == RewriteOperation.Remove)
            {
                return;
            }

            var created = new JsonObject();
            parent[segment] = created;
            parent = created;
        }

        string leaf = rule.Path[^1];
        switch (rule.Operation)
        {
            case RewriteOperation.Set:
                parent[leaf] = rule.Value?.DeepClone();
                break;
            case RewriteOperation.Remove:
                parent.Remove(leaf);
                break;
            case RewriteOperation.Default when !parent.ContainsKey(leaf):
                parent[leaf] = rule.Value?.DeepClone();
                break;
        }
    }

    private static bool IsHeaderToken(string name) =>
        name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private sealed record HeaderRule(RewriteOperation Operation, string Name, string Value);

    private sealed record BodyRule(RewriteOperation Operation, string[] Path, JsonNode? Value);
}
