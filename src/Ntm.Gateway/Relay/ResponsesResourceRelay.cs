using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 按 id 访问上游 Responses 资源（ADR-0020）：取回、删除、取消响应，列它的输入项，
/// UpstreamConversation 的增删改查，以及 <c>/v1/responses/input_tokens</c>。
/// 资源存在产出它的那个渠道密钥的账号里，所以只按 ResponseLanding 回到那里：不选路、不重试、不换候选（Q270），
/// 不看冷却、费用上限、时段、RPM、并发（Q274），也不写请求记录（Q275）。
/// 本网关没见过的 id 一律 404，绝不盲转（Q272）。
/// </summary>
public sealed partial class ResponsesResourceRelay(
    ConfigSnapshotProvider snapshots,
    CandidateSelector selector,
    CooldownRegistry cooldowns,
    UpstreamForwarder forwarder,
    ResponseLandingTable landings,
    ResponseUsageBackfill backfill,
    TimeProvider timeProvider,
    ILogger<ResponsesResourceRelay> logger)
{
    /// <summary>不是流的按 id 请求整个限时 10 秒（Q284）。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>流式取回：首字节和中途空闲沿用中继的流式超时（Q284）。</summary>
    public static readonly TimeSpan StreamFirstByteTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>回填用量时最多缓冲这么大的响应体；更大的直接流给客户端，不回填。</summary>
    private const int MaxBufferedBytes = 8 * 1024 * 1024;

    private static readonly InboundDialect Dialect = InboundDialect.OpenAiResponses;

    /// <summary>
    /// <c>/v1/responses/{id}</c>（GET、DELETE）、<c>/{id}/cancel</c>（POST）、<c>/{id}/input_items</c>（GET）。
    /// <paramref name="suffix"/> 是 id 后面那段，如 <c>/cancel</c>；没有时为空串。
    /// </summary>
    public Task HandleResponseAsync(HttpContext context, string responseId, string suffix)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseId);
        ArgumentNullException.ThrowIfNull(suffix);
        return HandleByIdAsync(context, ResponseLandingKind.Response, responseId, suffix);
    }

    /// <summary><c>/v1/conversations/{id}</c> 和它的 <c>/items</c>、<c>/items/{item}</c>（Q280）。</summary>
    public Task HandleConversationAsync(HttpContext context, string conversationId, string suffix)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(suffix);
        return HandleByIdAsync(context, ResponseLandingKind.Conversation, conversationId, suffix);
    }

    /// <summary>
    /// <c>POST /v1/conversations</c>（Q279）：在这个 API 密钥能用的 Responses 渠道里，按优先级、渠道 id
    /// 挑第一个有不在冷却的渠道密钥的，建在那里并记下落点。只试一次。
    /// </summary>
    public async Task HandleCreateConversationAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken aborted = context.RequestAborted;
        ConfigSnapshot snapshot = snapshots.Current;
        if (await AuthenticateAsync(context, snapshot).ConfigureAwait(false) is not { } token)
        {
            return;
        }

        byte[]? body = await ReadBodyOrRejectAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        (ChannelSnapshot Channel, ChannelKeySnapshot Key)? target = snapshot.Channels
            .Where(c => c.ApiFormat == ApiFormat.OpenAiResponses && token.AllowsChannel(c.ChannelId))
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.ChannelId)
            .Select(c => (Channel: c, Key: c.Keys
                .OrderByDescending(k => k.Priority)
                .ThenBy(k => k.ChannelKeyId)
                .FirstOrDefault(k => !cooldowns.IsKeyCooling(k.Fingerprint))))
            .Where(t => t.Key is not null && ResourceUri(t.Channel, ResponseLandingKind.Conversation, null, string.Empty, context.Request.QueryString) is not null)
            .Select(t => ((ChannelSnapshot, ChannelKeySnapshot)?)(t.Channel, t.Key!))
            .FirstOrDefault();
        if (target is not { } chosen)
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status503ServiceUnavailable,
                GatewayErrorKind.NoChannel,
                "没有能建对话的渠道：要 Responses 格式、这个 API 密钥能用、有不在冷却的渠道密钥，而且路径以 /responses 结尾。",
                aborted).ConfigureAwait(false);
            return;
        }

        Uri uri = ResourceUri(chosen.Channel, ResponseLandingKind.Conversation, null, string.Empty, context.Request.QueryString)!;
        ResponseLanding? remembered = null;
        await ForwardAsync(
            context,
            chosen.Channel,
            chosen.Key,
            HttpMethod.Post,
            uri,
            body,
            stream: false,
            onBody: reply =>
            {
                if (ReadId(reply.Span) is { } id)
                {
                    DateTimeOffset now = timeProvider.GetUtcNow();
                    remembered = new ResponseLanding
                    {
                        Id = id,
                        Kind = ResponseLandingKind.Conversation,
                        ChannelId = chosen.Channel.ChannelId,
                        KeyFingerprint = chosen.Key.Fingerprint,
                        ApiKeyId = token.ApiKeyId,
                        CreatedAt = now,
                        LastUsedAt = now,
                    };
                    landings.Remember(remembered);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

        if (remembered is not null)
        {
            LogConversationCreated(logger, remembered.Id, chosen.Channel.ChannelName);
        }
    }

    /// <summary>
    /// <c>POST /v1/responses/input_tokens</c>（Q282）：带 previous_response_id 或 conversation 时回到落点；
    /// 否则照常选路，只试第一个 Responses 候选。上游的错误原样转回，不本地估算，不写记录。
    /// </summary>
    public async Task HandleInputTokensAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken aborted = context.RequestAborted;
        ConfigSnapshot snapshot = snapshots.Current;
        if (await AuthenticateAsync(context, snapshot).ConfigureAwait(false) is not { } token)
        {
            return;
        }

        byte[]? body = await ReadBodyOrRejectAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        (string? model, string? previous, string? conversation, bool valid) = ReadInputTokensRequest(body);
        if (!valid)
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status400BadRequest, GatewayErrorKind.InvalidRequest,
                "请求体不是合法的 JSON 对象。", aborted).ConfigureAwait(false);
            return;
        }

        if (previous is not null && conversation is not null)
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status400BadRequest, GatewayErrorKind.InvalidRequest,
                "conversation 和 previous_response_id 只能用一个。", aborted).ConfigureAwait(false);
            return;
        }

        if (model is not null && !token.AllowsModel(model))
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, GatewayErrorKind.ModelNotAllowed,
                $"这个 API 密钥不能用模型 {model}。", aborted).ConfigureAwait(false);
            return;
        }

        ChannelSnapshot? channel;
        ChannelKeySnapshot? key;
        string? upstreamModel;
        if ((previous ?? conversation) is { } landingId)
        {
            ResponseLandingKind kind = previous is not null ? ResponseLandingKind.Response : ResponseLandingKind.Conversation;
            ResponseLanding? landing = await landings.FindAsync(landingId, kind, token.ApiKeyId, aborted).ConfigureAwait(false);
            if (landing is null)
            {
                await WriteUnknownAsync(context, kind, landingId).ConfigureAwait(false);
                return;
            }

            if (await ResolveLandingAsync(context, snapshot, token, landing).ConfigureAwait(false) is not { } resolved)
            {
                return;
            }

            (channel, key) = resolved;
            upstreamModel = landing.UpstreamModel
                ?? (model is null ? null : snapshot.FindRoute(model)?.PriorityGroups
                    .SelectMany(g => g.Candidates)
                    .FirstOrDefault(c => c.ChannelId == channel.ChannelId)?.UpstreamModel);
        }
        else
        {
            if (model is null)
            {
                await WriteErrorAsync(context.Response, StatusCodes.Status400BadRequest, GatewayErrorKind.InvalidRequest,
                    "缺少 model。", aborted).ConfigureAwait(false);
                return;
            }

            PublishedModelRoute? route = snapshot.FindRoute(model);
            if (route is null || route.RequestKind != RequestKind.Chat)
            {
                await WriteErrorAsync(context.Response, StatusCodes.Status404NotFound, GatewayErrorKind.ModelNotFound,
                    $"没有叫 {model} 的对话模型。", aborted).ConfigureAwait(false);
                return;
            }

            CandidatePlan plan = selector.Plan(route, c =>
                c.Channel.ApiFormat == ApiFormat.OpenAiResponses
                && token.AllowsChannel(c.ChannelId)
                && ResourceUri(c.Channel, null, null, "/input_tokens", context.Request.QueryString) is not null);
            (channel, key, upstreamModel) = (null, null, null);
            foreach (RouteCandidate candidate in plan.Candidates)
            {
                ChannelKeySnapshot? usable = candidate.Channel.Keys
                    .OrderByDescending(k => k.Priority)
                    .ThenBy(k => k.ChannelKeyId)
                    .FirstOrDefault(k => cooldowns.CheckKey(k.Fingerprint, candidate.UpstreamModel) is null);
                if (usable is not null)
                {
                    (channel, key, upstreamModel) = (candidate.Channel, usable, candidate.UpstreamModel);
                    break;
                }
            }

            if (channel is null || key is null)
            {
                await WriteErrorAsync(context.Response, StatusCodes.Status503ServiceUnavailable, GatewayErrorKind.NoChannel,
                    $"模型 {model} 现在没有能数 Token 的 Responses 渠道。", aborted).ConfigureAwait(false);
                return;
            }
        }

        Uri? uri = ResourceUri(channel, null, null, "/input_tokens", context.Request.QueryString);
        if (uri is null)
        {
            await WriteUnderivableAsync(context, channel).ConfigureAwait(false);
            return;
        }

        byte[] sent = upstreamModel is not null && OpenAiChatRequestPatcher.TryReplaceModel(body, upstreamModel, out byte[] renamed)
            ? renamed
            : body;
        await ForwardAsync(context, channel, key, HttpMethod.Post, uri, sent, stream: false, onBody: null).ConfigureAwait(false);
    }

    private async Task HandleByIdAsync(HttpContext context, ResponseLandingKind kind, string id, string suffix)
    {
        CancellationToken aborted = context.RequestAborted;
        ConfigSnapshot snapshot = snapshots.Current;
        if (await AuthenticateAsync(context, snapshot).ConfigureAwait(false) is not { } token)
        {
            return;
        }

        ResponseLanding? landing = await landings.FindAsync(id, kind, token.ApiKeyId, aborted).ConfigureAwait(false);
        if (landing is null)
        {
            await WriteUnknownAsync(context, kind, id).ConfigureAwait(false);
            return;
        }

        if (await ResolveLandingAsync(context, snapshot, token, landing).ConfigureAwait(false) is not { } resolved)
        {
            return;
        }

        (ChannelSnapshot channel, ChannelKeySnapshot key) = resolved;
        Uri? uri = ResourceUri(channel, kind, id, suffix, context.Request.QueryString);
        if (uri is null)
        {
            await WriteUnderivableAsync(context, channel).ConfigureAwait(false);
            return;
        }

        var method = new HttpMethod(context.Request.Method);
        byte[] body = [];
        if (method == HttpMethod.Post)
        {
            byte[]? read = await ReadBodyOrRejectAsync(context).ConfigureAwait(false);
            if (read is null)
            {
                return;
            }

            body = read;
        }

        bool stream = method == HttpMethod.Get
            && kind == ResponseLandingKind.Response
            && suffix.Length == 0
            && string.Equals(context.Request.Query["stream"], "true", StringComparison.OrdinalIgnoreCase);

        // 只有取回 background 响应本身、还等着回填用量时才缓冲响应体（Q277）。
        bool settle = method == HttpMethod.Get
            && kind == ResponseLandingKind.Response
            && suffix.Length == 0
            && !stream
            && landing.AwaitingUsage;

        int status = await ForwardAsync(
            context,
            channel,
            key,
            method,
            uri,
            body,
            stream,
            onBody: settle
                ? reply => backfill.TryBackfillAsync(landing, reply, CancellationToken.None)
                : null).ConfigureAwait(false);

        bool succeeded = status is >= 200 and < 300;
        if (kind == ResponseLandingKind.Response && method == HttpMethod.Delete && suffix.Length == 0)
        {
            // 上游确认删了：落点一起删（Q268 补充）。没删成就留着，客户端还能再试。
            if (succeeded)
            {
                landings.Forget(id);
            }

            LogDeleted(logger, id, channel.ChannelName, status);
        }
        else if (kind == ResponseLandingKind.Response && suffix == "/cancel")
        {
            LogCanceled(logger, id, channel.ChannelName, status);
        }
        else if (kind == ResponseLandingKind.Conversation && method == HttpMethod.Delete && suffix.Length == 0)
        {
            // 对话删了也留着落点到过期（Q280）：再来的请求由上游回答「不存在」，比网关说「不认识」更准。
            LogConversationDeleted(logger, id, channel.ChannelName, status);
        }
    }

    /// <summary>落点 → 现在的渠道和那把渠道密钥。用不了就 502，不换（Q273）；渠道不许用就 403（Q274）。</summary>
    private static async Task<(ChannelSnapshot Channel, ChannelKeySnapshot Key)?> ResolveLandingAsync(
        HttpContext context,
        ConfigSnapshot snapshot,
        ApiKeySnapshot token,
        ResponseLanding landing)
    {
        CancellationToken aborted = context.RequestAborted;
        if (landing.PublishedModel is { } published && !token.AllowsModel(published))
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, GatewayErrorKind.ModelNotAllowed,
                $"这个 API 密钥不能用模型 {published}。", aborted).ConfigureAwait(false);
            return null;
        }

        if (!token.AllowsChannel(landing.ChannelId))
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status403Forbidden, GatewayErrorKind.ChannelNotAllowed,
                "这个 API 密钥现在不能用产出它的那个渠道。", aborted).ConfigureAwait(false);
            return null;
        }

        ChannelSnapshot? channel = snapshot.Channels.FirstOrDefault(c => c.ChannelId == landing.ChannelId);
        ChannelKeySnapshot? key = channel is null ? null : KeyFor(channel, landing);
        if (channel is null || key is null)
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status502BadGateway, GatewayErrorKind.BadChannelConfig,
                channel is null
                    ? "产出它的那个渠道现在用不了（停用、删除或代理配置有误），网关不会换到别的渠道。"
                    : "产出它的那把渠道密钥现在用不了（停用、删除、换了值或解不开），网关不会换到别的密钥。",
                aborted).ConfigureAwait(false);
            return null;
        }

        return (channel, key);
    }

    /// <summary>
    /// 按 id 的请求用哪把渠道密钥：产出它的那把；渠道标了「密钥属于同一个账号」时它不在就换优先级最高的那把（Q306）。
    /// 只发一次，不管冷却和限流。
    /// </summary>
    internal static ChannelKeySnapshot? KeyFor(ChannelSnapshot channel, ResponseLanding landing)
    {
        ChannelKeySnapshot? producing = channel.Keys.FirstOrDefault(k => k.Fingerprint == landing.KeyFingerprint);
        if (producing is not null || !channel.KeysShareAccount)
        {
            return producing;
        }

        return channel.Keys.OrderByDescending(k => k.Priority).ThenBy(k => k.ChannelKeyId).FirstOrDefault();
    }

    /// <summary>
    /// 发出去，把上游的回答原样交给客户端。上游 401/403 换成 502：那是渠道密钥的问题，
    /// 客户端拿到 401 会以为自己的 API 密钥错了。<paramref name="onBody"/> 不为 null 时缓冲整个响应体，
    /// 成功时交给它（回填用量、记下新对话的落点）。返回上游的状态码，没拿到时为 0。
    /// </summary>
    private async Task<int> ForwardAsync(
        HttpContext context,
        ChannelSnapshot channel,
        ChannelKeySnapshot key,
        HttpMethod method,
        Uri uri,
        byte[] body,
        bool stream,
        Func<ReadOnlyMemory<byte>, Task>? onBody)
    {
        CancellationToken aborted = context.RequestAborted;
        HttpResponse response = context.Response;
        var request = new ForwardRequest
        {
            RequestUri = uri,
            Method = method,
            Proxy = channel.Proxy,
            Secret = key.Secret,
            UpstreamApiFormat = ApiFormat.OpenAiResponses,
            InboundApiFormat = ApiFormat.OpenAiResponses,
            Body = body,
            Rewriter = channel.Rewriter,
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        deadline.CancelAfter(stream ? StreamFirstByteTimeout : RequestTimeout);
        HttpResponseMessage upstream;
        try
        {
            upstream = await forwarder.SendRawAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            await WriteErrorAsync(response, StatusCodes.Status504GatewayTimeout, GatewayErrorKind.Timeout,
                $"渠道 {channel.ChannelName} 没在时限内回答。", aborted).ConfigureAwait(false);
            return 0;
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex, channel.ChannelName);
            await WriteErrorAsync(response, StatusCodes.Status502BadGateway, GatewayErrorKind.UpstreamFailed,
                $"连不上渠道 {channel.ChannelName}。", aborted).ConfigureAwait(false);
            return 0;
        }

        using (upstream)
        {
            int status = (int)upstream.StatusCode;
            if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            {
                await WriteErrorAsync(response, StatusCodes.Status502BadGateway, GatewayErrorKind.UpstreamFailed,
                    $"渠道 {channel.ChannelName} 拒绝了它的渠道密钥（HTTP {status}）。", aborted).ConfigureAwait(false);
                return status;
            }

            response.StatusCode = status;
            if (upstream.Content.Headers.ContentType is { } contentType)
            {
                response.ContentType = contentType.ToString();
            }

            try
            {
                await using Stream source = await upstream.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                if (stream)
                {
                    await CopyStreamingAsync(source, response, deadline, aborted).ConfigureAwait(false);
                }
                else if (onBody is not null && status is >= 200 and < 300)
                {
                    byte[]? buffered = await BufferAsync(source, deadline.Token).ConfigureAwait(false);
                    if (buffered is null)
                    {
                        await WriteErrorAsync(response, StatusCodes.Status502BadGateway, GatewayErrorKind.UpstreamFailed,
                            "上游的响应太大。", aborted).ConfigureAwait(false);
                        return 0;
                    }

                    await response.Body.WriteAsync(buffered, aborted).ConfigureAwait(false);
                    await onBody(buffered).ConfigureAwait(false);
                }
                else
                {
                    await source.CopyToAsync(response.Body, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                // 响应头已经发出去了，改不了状态码：断开，让客户端知道没收全。
                LogBodyTimedOut(logger, channel.ChannelName);
                context.Abort();
            }
            catch (HttpRequestException ex)
            {
                LogUnreachable(logger, ex, channel.ChannelName);
                context.Abort();
            }

            return status;
        }
    }

    /// <summary>流式取回：每读到一块就重置空闲计时；第一块之前是首字节时限。</summary>
    private static async Task CopyStreamingAsync(
        Stream source,
        HttpResponse response,
        CancellationTokenSource deadline,
        CancellationToken aborted)
    {
        await response.StartAsync(aborted).ConfigureAwait(false);
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
        {
            deadline.CancelAfter(StreamIdleTimeout);
            await response.Body.WriteAsync(chunk.AsMemory(0, read), aborted).ConfigureAwait(false);
            await response.Body.FlushAsync(aborted).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> BufferAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBufferedBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 从渠道的 Responses 路径推资源地址：<c>…/responses</c> → <c>…/responses/{id}{suffix}</c>，
    /// 对话把最后的 <c>/responses</c> 换成 <c>/conversations</c>（Q270）。<paramref name="kind"/> 为 null 时是
    /// <c>…/responses{suffix}</c>（input_tokens）。渠道路径的查询串保留，客户端的查询串接在后面。
    /// 自定义路径不以 <c>/responses</c> 结尾时推不出来，返回 null。
    /// </summary>
    internal static Uri? ResourceUri(
        ChannelSnapshot channel,
        ResponseLandingKind? kind,
        string? id,
        string suffix,
        QueryString clientQuery)
    {
        if (!Upstream.UpstreamAddress.TryBuildRequestUri(
                channel.BaseUrl, channel.Path, ApiFormat.OpenAiResponses, RequestKind.Chat, out Uri? responses))
        {
            return null;
        }

        string path = responses.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/responses", StringComparison.Ordinal))
        {
            return null;
        }

        string escaped = id is null ? string.Empty : "/" + Uri.EscapeDataString(id);
        path = kind switch
        {
            ResponseLandingKind.Conversation => path[..^"/responses".Length] + "/conversations" + escaped + suffix,
            ResponseLandingKind.Response => path + escaped + suffix,
            _ => path + suffix,
        };

        string query = string.Join(
            '&',
            new[] { responses.Query.TrimStart('?'), clientQuery.HasValue ? clientQuery.Value!.TrimStart('?') : string.Empty }
                .Where(q => q.Length > 0));
        return new UriBuilder(responses) { Path = path, Query = query }.Uri;
    }

    private async Task<ApiKeySnapshot?> AuthenticateAsync(HttpContext context, ConfigSnapshot snapshot)
    {
        string? presented = RelayAuthentication.ReadPresentedApiKey(context.Request);
        ApiKeySnapshot? token = presented is null ? null : snapshot.FindApiKey(SecretFingerprint.Compute(presented));
        if (token is null || token.IsExpired(timeProvider.GetUtcNow()))
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status401Unauthorized,
                presented is null ? GatewayErrorKind.MissingApiKey : GatewayErrorKind.InvalidApiKey,
                presented is null ? "缺少 API 密钥。" : "API 密钥无效或已过期。",
                context.RequestAborted).ConfigureAwait(false);
            return null;
        }

        return token;
    }

    private static async Task<byte[]?> ReadBodyOrRejectAsync(HttpContext context)
    {
        byte[]? body = await ChatRelay.ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(
                context.Response,
                StatusCodes.Status413PayloadTooLarge,
                GatewayErrorKind.BodyTooLarge,
                $"请求体超过 {ChatRelay.MaxRequestBodyBytes} 字节。",
                context.RequestAborted).ConfigureAwait(false);
        }

        return body;
    }

    private static Task WriteUnknownAsync(HttpContext context, ResponseLandingKind kind, string id) =>
        WriteErrorAsync(
            context.Response,
            StatusCodes.Status404NotFound,
            GatewayErrorKind.ResourceNotFound,
            kind == ResponseLandingKind.Conversation
                ? $"本网关不认识对话 {id}：不是经本网关、用这个 API 密钥建的，或者已经过了保留期。"
                : $"本网关不认识响应 {id}：不是本网关、用这个 API 密钥产出的，或者已经过了保留期。",
            context.RequestAborted);

    private static Task WriteUnderivableAsync(HttpContext context, ChannelSnapshot channel) =>
        WriteErrorAsync(
            context.Response,
            StatusCodes.Status502BadGateway,
            GatewayErrorKind.BadChannelConfig,
            $"渠道 {channel.ChannelName} 配了自定义路径，推不出这个端点（路径以 /responses 结尾才能推）。",
            context.RequestAborted);

    private static (string? Model, string? Previous, string? Conversation, bool Valid) ReadInputTokensRequest(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, false);
            }

            string? conversation = root.TryGetProperty("conversation", out JsonElement node)
                ? node.ValueKind switch
                {
                    JsonValueKind.String => node.GetString(),
                    JsonValueKind.Object when node.TryGetProperty("id", out JsonElement inner) && inner.ValueKind == JsonValueKind.String
                        => inner.GetString(),
                    _ => null,
                }
                : null;
            return (StringOf(root, "model"), StringOf(root, "previous_response_id"), conversation, true);
        }
        catch (JsonException)
        {
            return (null, null, null, false);
        }
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? ReadId(ReadOnlySpan<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? StringOf(document.RootElement, "id")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "删除响应 {ResponseId}（渠道 {Channel}）：HTTP {Status}")]
    private static partial void LogDeleted(ILogger logger, string responseId, string channel, int status);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Information, Message = "取消响应 {ResponseId}（渠道 {Channel}）：HTTP {Status}")]
    private static partial void LogCanceled(ILogger logger, string responseId, string channel, int status);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Information, Message = "在渠道 {Channel} 建了对话 {ConversationId}")]
    private static partial void LogConversationCreated(ILogger logger, string conversationId, string channel);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Information, Message = "删除对话 {ConversationId}（渠道 {Channel}）：HTTP {Status}")]
    private static partial void LogConversationDeleted(ILogger logger, string conversationId, string channel, int status);

    [LoggerMessage(EventId = 4205, Level = LogLevel.Warning, Message = "连不上渠道 {Channel}。")]
    private static partial void LogUnreachable(ILogger logger, Exception exception, string channel);

    [LoggerMessage(EventId = 4206, Level = LogLevel.Warning, Message = "渠道 {Channel} 的响应体没在时限内传完。")]
    private static partial void LogBodyTimedOut(ILogger logger, string channel);
}
