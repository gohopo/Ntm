using System.Net;
using System.Text.Json;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Gateway.Sync;

/// <summary>ModelSync 要打的上游：渠道的地址、格式和代理。</summary>
/// <param name="AnthropicBearerAuth">Anthropic 渠道用 Authorization: Bearer 带密钥（Q166），列模型时和发请求一致。</param>
public sealed record ModelListTarget(
    string BaseUrl,
    string? Path,
    ApiFormat ApiFormat,
    ProxyAddress? Proxy,
    bool AnthropicBearerAuth = false);

/// <summary>一次取清单的结果：成功时是去重后的模型名，失败时是给人看的原因。</summary>
public sealed record ModelListResult(IReadOnlyList<string>? Models, string? Error, int? HttpStatusCode = null)
{
    public bool Succeeded => Models is not null;
}

/// <summary>
/// 用一把 ChannelKey 从上游取模型清单（Q137）。认证头按渠道的 ApiFormat，走渠道的代理。
/// 两种格式都读 <c>data[].id</c>；Anthropic 按 <c>has_more</c> / <c>last_id</c> 翻页。
/// 别的形状一律算失败，不猜。
/// </summary>
public sealed class ModelListFetcher
{
    public const int MaxModels = 2000;
    public const int MaxModelNameLength = 300;

    private const int MaxBodyBytes = 5 * 1024 * 1024;
    private const int AnthropicPageSize = 1000;
    private const int MaxPages = 10;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Func<ProxyAddress?, HttpMessageInvoker> _invokers;

    public ModelListFetcher(UpstreamHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _invokers = factory.For;
    }

    /// <summary>测试用：所有请求走同一个 invoker。</summary>
    public ModelListFetcher(HttpMessageInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invokers = _ => invoker;
    }

    /// <summary>
    /// 列模型的地址。渠道填了路径就从路径推（<c>/api/v3/chat/completions</c> → <c>/api/v3/models</c>），
    /// 没填就用格式的默认路径。推不出来返回 null。
    /// </summary>
    public static Uri? ModelsUri(ModelListTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string? path = string.IsNullOrWhiteSpace(target.Path)
            ? ApiFormatEndpoints.ModelsPath(target.ApiFormat)
            : ApiFormatEndpoints.DeriveModelsPath(target.Path.Trim());
        if (path is null)
        {
            return null;
        }

        return Uri.TryCreate(target.BaseUrl.TrimEnd('/') + path, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    public async Task<ModelListResult> FetchAsync(ModelListTarget target, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(secret);

        Uri? baseUri = ModelsUri(target);
        if (baseUri is null)
        {
            return Fail("从渠道的请求路径推不出列模型的地址：路径要以 /chat/completions、/messages 或 /responses 结尾。");
        }

        HttpMessageInvoker invoker = _invokers(target.Proxy);
        string via = target.Proxy is null ? "" : $"经代理 {target.Proxy.Endpoint}：";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        List<string> models = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? after = null;
        bool paginate = target.ApiFormat == ApiFormat.Anthropic;

        for (int page = 0; page < MaxPages; page++)
        {
            Uri uri = paginate ? WithPage(baseUri, after) : baseUri;
            Page? read;
            try
            {
                (read, ModelListResult? failure) = await ReadPageAsync(invoker, uri, target.ApiFormat, target.AnthropicBearerAuth, secret, via, timeout.Token).ConfigureAwait(false);
                if (failure is not null)
                {
                    return failure;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail($"{via}上游 {Timeout.TotalSeconds:0} 秒内没有返回模型清单。");
            }
            catch (HttpRequestException ex)
            {
                return Fail($"{via}连不上上游：{ex.Message}");
            }
            catch (IOException ex)
            {
                return Fail($"{via}读取模型清单时连接断了：{ex.Message}");
            }

            foreach (string id in read!.Ids)
            {
                if (seen.Add(id))
                {
                    models.Add(id);
                }
            }

            if (models.Count > MaxModels)
            {
                return Fail($"上游列出了超过 {MaxModels} 个模型，ModelSync 不接收这么长的清单。");
            }

            if (!paginate || !read.HasMore || read.LastId is null)
            {
                return models.Count == 0
                    ? Fail("上游返回了一个空的模型清单。为了不把已有清单清空，这次不算成功。")
                    : new ModelListResult(models, null);
            }

            after = read.LastId;
        }

        return Fail($"上游的模型清单翻了 {MaxPages} 页还没到底，这次不算成功。");
    }

    private static async Task<(Page? Page, ModelListResult? Failure)> ReadPageAsync(
        HttpMessageInvoker invoker,
        Uri uri,
        ApiFormat format,
        bool anthropicBearer,
        string secret,
        string via,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApiFormatAuthScheme scheme = ApiFormatAuth.For(format, anthropicBearer);
        request.Headers.TryAddWithoutValidation(scheme.HeaderName, scheme.ValuePrefix is null ? secret : scheme.ValuePrefix + secret);
        foreach ((string name, string value) in scheme.AdditionalHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using HttpResponseMessage response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[]? body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired && via.Length > 0)
        {
            return (null, Fail($"{via}代理要求认证，或者用户名密码不对（HTTP 407）。", 407));
        }

        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            string snippet = body is null ? "" : Snippet(body);
            return (null, Fail($"{via}上游返回 HTTP {status}{(snippet.Length > 0 ? "：" + snippet : "")}", status));
        }

        if (body is null)
        {
            return (null, Fail($"上游的模型清单超过 {MaxBodyBytes / 1024 / 1024} MB。"));
        }

        Page? page = Parse(body);
        return page is null ? (null, Fail("无法识别的模型列表：上游返回的不是 {\"data\": [{\"id\": …}]} 这种形状。")) : (page, null);
    }

    /// <summary>读响应体，超过上限返回 null。</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Page? Parse(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<string> ids = [];
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                // 名字太长或带控制字符的条目跳过：存不进清单，也不会是正经的模型名。
                string? name = id.GetString()?.Trim();
                if (!string.IsNullOrEmpty(name) && name.Length <= MaxModelNameLength && !name.Any(char.IsControl))
                {
                    ids.Add(name);
                }
            }

            bool hasMore = root.TryGetProperty("has_more", out JsonElement more) && more.ValueKind == JsonValueKind.True;
            string? lastId = root.TryGetProperty("last_id", out JsonElement last) && last.ValueKind == JsonValueKind.String
                ? last.GetString()
                : null;
            return new Page(ids, hasMore, lastId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri WithPage(Uri uri, string? after)
    {
        string query = $"limit={AnthropicPageSize}" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
        var builder = new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? query : uri.Query.TrimStart('?') + "&" + query };
        return builder.Uri;
    }

    private static string Snippet(byte[] body)
    {
        string text = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 600)).ReplaceLineEndings(" ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static ModelListResult Fail(string error, int? status = null) => new(null, error, status);

    private sealed record Page(IReadOnlyList<string> Ids, bool HasMore, string? LastId);
}
