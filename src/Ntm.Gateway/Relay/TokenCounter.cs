using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;

namespace Ntm.Gateway.Relay;

/// <summary>
/// <c>POST /v1/messages/count_tokens</c>（Q251–Q255，ADR-0019）。
/// <list type="bullet">
/// <item>鉴权和 <c>/v1/messages</c> 一样，只看 API 密钥本身、模型限制和渠道黑白名单；不占 RPM、并发，不受费用上限约束。</item>
/// <item>按正常选路顺序挑第一个 Anthropic 格式的候选，只试它一次；上游不是 2xx 就本地估算，客户端永远拿到一个数。</item>
/// <item>上游 404/405 说明它没有这个端点，记 24 小时，期间直接估算。不冷却，不影响健康度。</item>
/// <item>不写请求日志、统计和进行中列表：调用很频繁，又不花钱。</item>
/// </list>
/// </summary>
public sealed partial class TokenCounter(
    ConfigSnapshotProvider snapshots,
    CandidateSelector selector,
    CooldownRegistry cooldowns,
    UpstreamForwarder forwarder,
    TimeProvider timeProvider,
    ILogger<TokenCounter> logger)
{
    /// <summary>响应头：<c>upstream</c> 是上游算的，<c>estimated</c> 是网关估的。</summary>
    public const string SourceHeader = "x-ntm-token-count";

    /// <summary>估算往多里放 10%：估少了客户端会以为上下文还够，结果超长（Q254）。</summary>
    public const double EstimateMargin = 1.1;

    public static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan UnsupportedFor = TimeSpan.FromHours(24);

    private const int MaxResponseBytes = 64 * 1024;

    /// <summary>count_tokens 认的字段；其余（max_tokens、stream、temperature…）发过去会被拒（Q252）。</summary>
    private static readonly string[] CountFields =
        ["messages", "system", "tools", "tool_choice", "thinking", "output_config", "cache_control"];

    private static readonly InboundDialect Dialect = InboundDialect.Anthropic;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConcurrentDictionary<int, DateTimeOffset> _unsupported = new();

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CancellationToken aborted = context.RequestAborted;
        DateTimeOffset now = timeProvider.GetUtcNow();
        ConfigSnapshot snapshot = snapshots.Current;

        string? presented = RelayAuthentication.ReadPresentedApiKey(context.Request);
        ApiKeySnapshot? token = presented is null ? null : snapshot.FindApiKey(SecretFingerprint.Compute(presented));
        if (token is null || token.IsExpired(now))
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status401Unauthorized,
                presented is null ? GatewayErrorKind.MissingApiKey : GatewayErrorKind.InvalidApiKey,
                presented is null ? "缺少 API 密钥。" : "API 密钥无效或已过期。",
                aborted).ConfigureAwait(false);
            return;
        }

        byte[]? body = await ChatRelay.ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status413PayloadTooLarge,
                GatewayErrorKind.BodyTooLarge,
                $"请求体超过 {ChatRelay.MaxRequestBodyBytes} 字节。",
                aborted).ConfigureAwait(false);
            return;
        }

        AnthropicRequestReading reading = AnthropicRequestReader.Read(body);
        if (reading.Request is not { } request)
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status400BadRequest,
                GatewayErrorKind.InvalidRequest,
                reading.Error ?? "请求体不是合法的 Anthropic 请求。",
                aborted).ConfigureAwait(false);
            return;
        }

        if (!token.AllowsModel(request.Model))
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status403Forbidden,
                GatewayErrorKind.ModelNotAllowed,
                $"这个 API 密钥不能用模型 {request.Model}。",
                aborted).ConfigureAwait(false);
            return;
        }

        PublishedModelRoute? route = snapshot.FindRoute(request.Model);
        if (route is null || route.RequestKind != RequestKind.Chat)
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status404NotFound,
                GatewayErrorKind.ModelNotFound,
                $"没有叫 {request.Model} 的对话模型。",
                aborted).ConfigureAwait(false);
            return;
        }

        long? counted = null;
        CandidatePlan plan = selector.Plan(route, c =>
            c.Channel.ApiFormat == ApiFormat.Anthropic
            && token.AllowsChannel(c.ChannelId)
            && !IsUnsupported(c.ChannelId, now)
            && CountUri(c.Channel) is not null);
        foreach (RouteCandidate candidate in plan.Candidates)
        {
            ChannelKeySnapshot? key = candidate.Channel.Keys
                .OrderByDescending(k => k.Priority)
                .ThenBy(k => k.ChannelKeyId)
                .FirstOrDefault(k => cooldowns.CheckKey(k.Fingerprint, candidate.UpstreamModel) is null);
            if (key is null)
            {
                continue;
            }

            // 只试第一个能用的候选（Q252）。
            counted = await AskUpstreamAsync(context, body, candidate, key, now, aborted).ConfigureAwait(false);
            break;
        }

        string source = counted is null ? "estimated" : "upstream";
        long tokens = counted ?? Estimate(body);
        LogCounted(logger, request.Model, tokens, source);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.Headers[SourceHeader] = source;
        await context.Response.Body.WriteAsync(Answer(tokens), aborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 渠道的 count_tokens 地址：没填路径用默认；路径以 <c>/messages</c> 结尾就接上 <c>/count_tokens</c>，保留查询串；
    /// 其他自定义路径推不出来，返回 null（Q252）。
    /// </summary>
    public static Uri? CountUri(ChannelSnapshot channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!Upstream.UpstreamAddress.TryBuildRequestUri(channel.BaseUrl, channel.Path, channel.ApiFormat, RequestKind.Chat, out Uri? messages))
        {
            return null;
        }

        string path = messages.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/messages", StringComparison.Ordinal))
        {
            return null;
        }

        var builder = new UriBuilder(messages) { Path = path + "/count_tokens" };
        return builder.Uri;
    }

    /// <summary>本地估算：字符估算乘 1.1，向上取整（Q254）。只数 count_tokens 认的那些字段。</summary>
    public static long Estimate(ReadOnlySpan<byte> body)
    {
        byte[] trimmed = CountBody(body, model: null) ?? body.ToArray();
        long raw = UsageEstimator.Estimate(trimmed, 0, 0).InputTokens;
        return (long)Math.Ceiling(raw * EstimateMargin);
    }

    private bool IsUnsupported(int channelId, DateTimeOffset now) =>
        _unsupported.TryGetValue(channelId, out DateTimeOffset until) && until > now;

    private async Task<long?> AskUpstreamAsync(
        HttpContext context,
        byte[] body,
        RouteCandidate candidate,
        ChannelKeySnapshot key,
        DateTimeOffset now,
        CancellationToken aborted)
    {
        ChannelSnapshot channel = candidate.Channel;
        Uri? uri = CountUri(channel);
        byte[]? sent = CountBody(body, candidate.UpstreamModel);
        if (uri is null || sent is null)
        {
            return null;
        }

        var request = new ForwardRequest
        {
            RequestUri = uri,
            Proxy = channel.Proxy,
            Secret = key.Secret,
            UpstreamApiFormat = ApiFormat.Anthropic,
            InboundApiFormat = ApiFormat.Anthropic,
            AnthropicBearerAuth = channel.AnthropicBearerAuth,
            Body = sent,
            ForwardedHeaders = ChatRelay.ForwardedAnthropicHeaders(context.Request, ApiFormat.Anthropic, ApiFormat.Anthropic),
            Rewriter = channel.Rewriter,
        };

        (int status, byte[]? reply) = await forwarder
            .ExchangeAsync(request, UpstreamTimeout, MaxResponseBytes, aborted)
            .ConfigureAwait(false);

        if (status is StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)
        {
            _unsupported[channel.ChannelId] = now + UnsupportedFor;
            LogUnsupported(logger, channel.ChannelName, status);
            return null;
        }

        if (reply is null || ReadCount(reply) is not { } tokens)
        {
            LogNoAnswer(logger, channel.ChannelName, status);
            return null;
        }

        return tokens;
    }

    /// <summary>只留 count_tokens 认的字段；<paramref name="model"/> 为 null 时不写 model。不是 JSON 对象返回 null。</summary>
    private static byte[]? CountBody(ReadOnlySpan<byte> body, string? model)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var buffer = new ArrayBufferWriter<byte>(body.Length + 64);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                if (model is not null)
                {
                    writer.WriteString("model", model);
                }

                foreach (string field in CountFields)
                {
                    if (root.TryGetProperty(field, out JsonElement value))
                    {
                        writer.WritePropertyName(field);
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadCount(byte[] reply)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("input_tokens", out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt64(out long value)
                && value >= 0
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Answer(long tokens)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("input_tokens", tokens);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static async Task WriteErrorAsync(
        HttpResponse response,
        int status,
        GatewayErrorKind kind,
        string message,
        CancellationToken cancellationToken)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.Body.WriteAsync(Dialect.ErrorBody(kind, message), cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Debug, Message = "count_tokens {Model}：{Tokens}（{Source}）")]
    private static partial void LogCounted(ILogger logger, string model, long tokens, string source);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "渠道 {Channel} 不支持 count_tokens（HTTP {Status}），24 小时内改用本地估算。")]
    private static partial void LogUnsupported(ILogger logger, string channel, int status);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Debug, Message = "渠道 {Channel} 的 count_tokens 没给出结果（HTTP {Status}），改用本地估算。")]
    private static partial void LogNoAnswer(ILogger logger, string channel, int status);
}
