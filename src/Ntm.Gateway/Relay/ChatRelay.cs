using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Live;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Traffic;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 中继：认证、选候选、逐个 Attempt、记录 Request。与入站格式无关——
/// <c>POST /v1/chat/completions</c>、<c>/v1/messages</c>、<c>/v1/responses</c> 走同一副骨架，
/// 格式相关的部分（读请求、给上游的字节、错误形状）由 <see cref="InboundDialect"/> 提供。
/// 名字沿用「对话」，但嵌入和图片生成（<c>/v1/embeddings</c>、<c>/v1/images/generations</c>）也走这里（Q225，ADR-0018）：
/// 它们只在 OpenAI 格式之间透传，没有 Affinity，没有 previous_response_id。
///
/// 当前范围：模型下发、按 Priority 分档的加权选路、四作用域冷却、计价、API 密钥与渠道的 Restriction、Affinity、实时链路与中断。
/// 后续切片不改这里的骨架：重试只发生在提交点之前（ADR-0008），每次尝试都落一条 Attempt。
/// </summary>
public sealed partial class ChatRelay(
    ConfigSnapshotProvider snapshots,
    CandidateSelector selector,
    CooldownRegistry cooldowns,
    HealthTracker health,
    UpstreamForwarder forwarder,
    TrafficRecorder recorder,
    RestrictionChecker restrictions,
    InFlightRegistry inFlight,
    AffinityTable affinity,
    ResponseLandingTable responseLandings,
    Debugging.DebugCaptureSink debugSink,
    ThinkingStyleMemory thinkingStyles,
    TimeProvider timeProvider,
    ILogger<ChatRelay> logger)
{
    /// <summary>请求体上限。Kestrel 默认约 28.6 MB，这里显式放到与解析器一致的 32 MiB。</summary>
    public const int MaxRequestBodyBytes = 32 * 1024 * 1024;

    /// <summary>一个 Request 最多几次 Attempt（Q30）。</summary>
    public const int MaxAttempts = 5;

    public const string RequestIdHeader = "x-ntm-request-id";

    public async Task HandleAsync(HttpContext context, InboundDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dialect);

        HttpResponse response = context.Response;
        CancellationToken aborted = context.RequestAborted;
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        ConfigSnapshot snapshot = snapshots.Current;

        // 认证失败不落记录：未认证的流量写盘会变成用磁盘做的拒绝服务。
        string? presented = RelayAuthentication.ReadPresentedApiKey(context.Request);
        ApiKeySnapshot? token = presented is null
            ? null
            : snapshot.FindApiKey(SecretFingerprint.Compute(presented));

        if (token is null || token.IsExpired(startedAt))
        {
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status401Unauthorized,
                presented is null ? GatewayErrorKind.MissingApiKey : GatewayErrorKind.InvalidApiKey,
                presented is null ? "缺少 API 密钥。" : "API 密钥无效或已过期。",
                aborted).ConfigureAwait(false);
            return;
        }

        byte[]? body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status413PayloadTooLarge,
                GatewayErrorKind.BodyTooLarge,
                $"请求体超过 {MaxRequestBodyBytes} 字节。",
                aborted).ConfigureAwait(false);
            return;
        }

        await RunAsync(context, dialect, snapshot, startedAt, body, token, pinned: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Workbench 发的请求（Q200–Q213）：调用方已经用管理员登录态鉴过权，这里没有 API 密钥。
    /// <paramref name="pinned"/> 为 null 时按下发模型正常选路（模型测试、以下发模型为目标的对话）；
    /// 不为 null 时固定发给这个渠道，上游模型就是请求体里的 model（渠道测试、以渠道为目标的对话）。
    /// 失败不写 Cooldown 也不写 Health；固定渠道时成功清掉这条路的冷却（Q210）。原文一律录（Q213）。
    /// </summary>
    public async Task HandleWorkbenchAsync(HttpContext context, InboundDialect dialect, ChannelSnapshot? pinned)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dialect);

        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        ConfigSnapshot snapshot = snapshots.Current;

        byte[]? body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(
                context.Response,
                dialect,
                StatusCodes.Status413PayloadTooLarge,
                GatewayErrorKind.BodyTooLarge,
                $"请求体超过 {MaxRequestBodyBytes} 字节。",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await RunAsync(context, dialect, snapshot, startedAt, body, token: null, pinned).ConfigureAwait(false);
    }

    private async Task RunAsync(
        HttpContext context,
        InboundDialect dialect,
        ConfigSnapshot snapshot,
        DateTimeOffset startedAt,
        byte[] body,
        ApiKeySnapshot? token,
        ChannelSnapshot? pinned)
    {
        HttpResponse response = context.Response;
        CancellationToken aborted = context.RequestAborted;

        InboundParseResult parsed = dialect.Parse(body);
        if (parsed.Request is not InboundRequest request)
        {
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status400BadRequest,
                GatewayErrorKind.InvalidRequest,
                parsed.Error ?? "请求无效。",
                aborted).ConfigureAwait(false);
            return;
        }

        var record = new RequestRecord
        {
            Id = Guid.CreateVersion7(startedAt),
            StartedAt = startedAt,
            ApiKeyId = token?.ApiKeyId,
            ApiKeyName = token?.Name,
            Origin = token is null ? RequestOrigin.Workbench : RequestOrigin.Client,
            PublishedModel = request.Model,
            InboundApiFormat = dialect.Format,
            RequestKind = dialect.Kind,
            IsStream = request.IsStream,
            Status = RequestStatus.Running,
        };

        response.Headers[RequestIdHeader] = record.Id.ToString();
        InFlightRequest tracked = inFlight.Register(record);

        try
        {
            await RelayAsync(context, dialect, snapshot, record, request, token, pinned, tracked, body).ConfigureAwait(false);
        }
        finally
        {
            Finish(record, tracked.InterruptRequested, aborted);
            inFlight.Complete(record);
        }
    }

    private async Task RelayAsync(
        HttpContext context,
        InboundDialect dialect,
        ConfigSnapshot snapshot,
        RequestRecord record,
        InboundRequest request,
        ApiKeySnapshot? token,
        ChannelSnapshot? pinned,
        InFlightRequest tracked,
        byte[] body)
    {
        HttpResponse response = context.Response;
        CancellationToken aborted = context.RequestAborted;

        // 脱敏要用：客户端出示的那把 API 密钥的原文，只在内存里待一会儿（Q158）。
        string? presentedSecret = RelayAuthentication.ReadPresentedApiKey(context.Request);

        // 转发用的令牌：客户端断开或 Admin 中断都停（Q115）。写错误给客户端仍用 aborted——中断时客户端还在等回复。
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(aborted, tracked.InterruptToken);
        CancellationToken stop = stopping.Token;

        // API 密钥自己的限制，在找路由之前，按费用 → 模型 → 并发的顺序（Q90）。被挡住的也落记录、算失败（Q85）。
        if (token is not null && token.CostLimitNanoUsd is { } costLimit && restrictions.Costs.ApiKeyTotal(token.ApiKeyId) >= costLimit)
        {
            await RejectAsync(
                record, response, dialect, StatusCodes.Status429TooManyRequests, GatewayErrorKind.QuotaExhausted,
                FailureClass.QuotaBlocked, "API 密钥的费用上限已用完", "API 密钥的费用上限已用完。", aborted).ConfigureAwait(false);
            return;
        }

        if (token is not null && !token.AllowsModel(request.Model))
        {
            await RejectAsync(
                record, response, dialect, StatusCodes.Status403Forbidden, GatewayErrorKind.ModelNotAllowed,
                FailureClass.RestrictionBlocked, $"API 密钥不允许模型 {request.Model}",
                $"这个 API 密钥不允许使用模型 {request.Model}。", aborted).ConfigureAwait(false);
            return;
        }

        // 一个 Request 从头到尾占一个位置：流结束、客户端断开、被中断时随 using 归还（Q80）。
        using IDisposable? apiKeyLease = token is null
            ? null
            : restrictions.Concurrency.ApiKeys.TryAcquire(token.ApiKeyId, token.MaxConcurrency);
        if (token is not null && apiKeyLease is null)
        {
            await RejectAsync(
                record, response, dialect, StatusCodes.Status429TooManyRequests, GatewayErrorKind.RateLimited,
                FailureClass.RestrictionBlocked, $"API 密钥的并发已满（上限 {token.MaxConcurrency}）",
                "这个 API 密钥同时进行的请求太多了。", aborted).ConfigureAwait(false);
            return;
        }

        if (pinned is not null)
        {
            await RelayPinnedAsync(context, dialect, snapshot, record, request, pinned, tracked, body).ConfigureAwait(false);
            return;
        }

        // 客户端说的名字必须是一个已启用的 PublishedModel。没下发就是 404：
        // 这不是「上游坏了」，重试别的候选也不会变好。
        PublishedModelRoute? route = snapshot.FindRoute(request.Model);
        if (route is not null && route.RequestKind != dialect.Kind)
        {
            // 用错了端点：模型下发了，只是不是这类请求（Q222）。说清该用哪个端点，别让人以为模型没了。
            record.HttpStatusCode = StatusCodes.Status404NotFound;
            record.BlockDetail = $"模型是{KindName(route.RequestKind)}模型，请求打到了{KindName(dialect.Kind)}端点";
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status404NotFound,
                GatewayErrorKind.ModelNotFound,
                $"模型 {request.Model} 是{KindName(route.RequestKind)}模型，请改用 {EndpointOf(route.RequestKind)}。",
                aborted).ConfigureAwait(false);
            return;
        }

        if (route is null)
        {
            record.HttpStatusCode = StatusCodes.Status404NotFound;
            record.BlockDetail = "模型没有下发";
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status404NotFound,
                GatewayErrorKind.ModelNotFound,
                $"模型 {request.Model} 没有下发。",
                aborted).ConfigureAwait(false);
            return;
        }

        // previous_response_id 与 conversation（ADR-0016、ADR-0020）：上游的状态存在产出它的那个账号里，
        // 只能回到那里，不换候选。只认同一个 API 密钥产出的（Q271），Workbench 按 0 算。
        ResponseLanding? landing = null;
        string landingLabel = "previous_response_id";
        int landingApiKeyId = token?.ApiKeyId ?? 0;
        if (request.PreviousResponseId is { } previousResponseId)
        {
            landing = await responseLandings.FindAsync(previousResponseId, ResponseLandingKind.Response, landingApiKeyId, aborted)
                .ConfigureAwait(false);
            if (landing is null)
            {
                await RejectStaleResponseAsync(
                    record, response, dialect, landingLabel,
                    "previous_response_id 不在落点表里（过了保留期、不是这个 API 密钥产出的，或不是本网关产出的）",
                    aborted).ConfigureAwait(false);
                return;
            }
        }
        else if (request.UpstreamConversationId is { } conversationId)
        {
            landingLabel = "conversation";
            landing = await responseLandings.FindAsync(conversationId, ResponseLandingKind.Conversation, landingApiKeyId, aborted)
                .ConfigureAwait(false);
            if (landing is null)
            {
                await RejectStaleResponseAsync(
                    record, response, dialect, landingLabel,
                    "conversation 不在落点表里（过了保留期、不是这个 API 密钥建的，或不是经本网关建的）",
                    aborted).ConfigureAwait(false);
                return;
            }
        }

        // Affinity（Q19/Q118）：同一个 Conversation 优先回到上次的渠道和渠道密钥。
        // Workbench 的 API 密钥按 0 算（Q211）：客户端的请求都带 API 密钥，两边的 Affinity 不会混在一起。
        // 嵌入和图片没有 Conversation，不做 Affinity（Q225）。
        UInt128? conversation = dialect.Kind == RequestKind.Chat
            ? ConversationKey.Compute(dialect.Format, body, token?.ApiKeyId, route.Name)
            : null;
        AffinityPin? pin = landing is { UpstreamModel: { } landedModel }
            ? new AffinityPin(landing.ChannelId, landedModel, landing.KeyFingerprint)
            : conversation is { } known ? affinity.Find(known) : null;

        // 转给 Responses 上游时，用 Conversation 的身份当提示缓存的分组键（Q192）。
        request.PromptCacheKey = conversation is { } identity
            ? identity.ToString("x32", CultureInfo.InvariantCulture)
            : null;

        // 能力在建快照时按 RequestKind 过滤过了，这里过滤格式（这个请求能不能被该上游服务，
        // 带着对方表达不了的参数就绕开，ADR-0014），再按 API 密钥的渠道白名单 / 黑名单（Q83）。
        bool excludedByApiKey = false;
        string? unservableReason = null;
        CandidatePlan plan = selector.Plan(route, c =>
        {
            // 接着上一次响应的请求只认产出它的那个候选（ADR-0016）；conversation 只认建它的渠道（Q278）。
            if (landing is not null
                && (c.ChannelId != landing.ChannelId
                    || (landing.UpstreamModel is { } landedUpstream && c.UpstreamModel != landedUpstream)))
            {
                return false;
            }

            if (!request.CanBeServedBy(c.Channel.ApiFormat, out string? reason))
            {
                unservableReason ??= reason is null ? null : $"渠道 {c.Channel.ChannelName}：{reason}";
                return false;
            }

            // 渠道存的是对话路径，别的请求从它推导；自定义路径推不出来就不当候选（Q224）。
            if (dialect.Kind != RequestKind.Chat && !TryBuildUpstreamUri(c.Channel, dialect, out _))
            {
                unservableReason ??= $"渠道 {c.Channel.ChannelName}：{UnderivablePath(dialect.Kind)}";
                return false;
            }

            if (token is not null && !token.AllowsChannel(c.ChannelId))
            {
                excludedByApiKey = true;
                return false;
            }

            return true;
        }, pin);

        foreach (CandidateBlock blocked in plan.Blocks)
        {
            record.Skip(
                blocked.Candidate.ChannelId,
                blocked.Candidate.Channel.ChannelName,
                blocked.Candidate.UpstreamModel,
                blocked.Reason,
                blocked.RecoversAt);
        }

        if (landing is not null && plan.Candidates.Count == 0)
        {
            string why = plan.Blocks.Count > 0
                ? landingLabel + " 的落点现在用不了：" + DescribeBlocks(plan, excludedByApiKey)
                : landingLabel + " 的落点已经不在这个模型的候选里";
            await RejectStaleResponseAsync(record, response, dialect, landingLabel, why, aborted).ConfigureAwait(false);
            return;
        }

        if (plan.Candidates.Count == 0)
        {
            record.BlockDetail = DescribeBlocks(plan, excludedByApiKey);
            if (unservableReason is not null)
            {
                // 只有「接不了这个请求」时，原因本身就是全部说明；和别的挡法并存时放在最前面。
                record.BlockDetail = plan.Blocks.Count == 0 && !excludedByApiKey
                    ? Truncate(unservableReason)
                    : Truncate(unservableReason + "；" + record.BlockDetail);
            }

            if (plan.AllCooling && plan.EarliestRecovery is { } recovery)
            {
                // 候选都在冷却：告诉客户端什么时候再来，而不是含糊地说没有渠道。
                int seconds = SecondsUntil(recovery);
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                record.HttpStatusCode = StatusCodes.Status503ServiceUnavailable;
                await WriteErrorAsync(
                    response,
                    dialect,
                    StatusCodes.Status503ServiceUnavailable,
                    GatewayErrorKind.AllCooling,
                    $"模型 {route.Name} 的候选都在冷却，请 {seconds} 秒后重试。",
                    aborted).ConfigureAwait(false);
                return;
            }

            // Restriction 挡住的也是「没有渠道」，但能算出最早恢复时刻的就告诉客户端（Q91）；
            // 冷却与 Restriction 混在一起时取最早的，有卡在并发上的就不给。
            string message = unservableReason is not null && plan.Blocks.Count == 0 && !excludedByApiKey
                ? $"模型 {route.Name} 没有能接这个请求的渠道（{unservableReason.TrimEnd('。')}）。"
                : $"模型 {route.Name} 没有可用的渠道。";
            if (plan.HasRestriction || (plan.Blocks.Count == 0 && excludedByApiKey))
            {
                record.BlockedBy = FailureClass.RestrictionBlocked;
                message = $"模型 {route.Name} 的候选渠道现在都受限制。";
            }

            if (plan.EarliestRecovery is { } restrictionRecovery)
            {
                int seconds = SecondsUntil(restrictionRecovery);
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                message += $"请 {seconds} 秒后重试。";
            }

            record.HttpStatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status503ServiceUnavailable,
                GatewayErrorKind.NoChannel,
                message,
                aborted).ConfigureAwait(false);
            return;
        }

        using var committer = new ResponseCommitter(response, timeProvider);
        committer.OnCommitted = () =>
            tracked.Committed(record.Attempts.Count, timeProvider.GetUtcNow());
        ForwardOutcome? last = null;

        // 选路之后、发出之前，渠道或密钥可能刚好满了（Q92）：跳过，不算失败。全部跳过时要知道是这个原因。
        List<string> skipped = [];

        foreach (RouteCandidate candidate in plan.Candidates)
        {
            if (record.Attempts.Count >= MaxAttempts || !ShouldTryAnother(last, dialect.Kind) || tracked.InterruptRequested)
            {
                break;
            }

            ChannelSnapshot channel = candidate.Channel;
            if (!TryBuildUpstreamUri(channel, dialect, out Uri? requestUri))
            {
                continue;
            }

            string upstreamModel = candidate.UpstreamModel;

            // 前面的尝试可能刚好把这个渠道今天的费用花到了上限。
            if (restrictions.CheckChannel(channel) is { } channelBlock)
            {
                skipped.Add($"渠道 {channel.ChannelName}：{RestrictionChecker.Describe(channelBlock.Reason)}");
                record.Skip(channel.ChannelId, channel.ChannelName, upstreamModel, channelBlock.Reason, channelBlock.RecoversAt);
                continue;
            }

            UpstreamPlan sendPlan = request.PlanFor(channel, upstreamModel, record.Id, thinkingStyles.Find(channel, upstreamModel));

            // 记住的那把渠道密钥排第一；它用不了时先换同渠道的其他密钥（Q119）。
            // Responses 落点只能用产出它的那把，除非渠道标了「密钥属于同一个账号」（Q306）：那时产出它的排第一，其余照常可换。
            bool pinnedChannel = pin is not null && pin.ChannelId == channel.ChannelId && pin.UpstreamModel == upstreamModel;
            List<ChannelKeySnapshot> keys = [.. channel.Keys
                .Where(k => landing is null || channel.KeysShareAccount || k.Fingerprint == landing.KeyFingerprint)
                .OrderByDescending(k => landing is not null && k.Fingerprint == landing.KeyFingerprint)
                .ThenByDescending(k => pinnedChannel && k.Fingerprint == pin!.KeyFingerprint)
                .ThenByDescending(k => k.Priority)
                .ThenBy(k => k.ChannelKeyId)];

            // 每把密钥都被密钥级 Restriction 挡掉、一次也没发时，这个候选记成跳过（Q308）。
            int keysTried = 0;
            int keysRefused = 0;
            (BlockReason Reason, DateTimeOffset? RecoversAt)? keyBlock = null;

            // 思考写法被上游拒绝时，同一把密钥换写法重发一次（Q315）。
            bool thinkingRetried = false;
            bool retryingThinking = false;
            for (int k = 0; k < keys.Count; k++)
            {
                ChannelKeySnapshot key = keys[k];
                bool retry = retryingThinking;
                retryingThinking = false;
                if (record.Attempts.Count >= MaxAttempts || (!retry && !ShouldTryAnother(last, dialect.Kind)) || tracked.InterruptRequested)
                {
                    break;
                }

                keysTried++;

                // 先占 Restriction（白名单、并发、这一分钟的一次），再占冷却：冷却的探测权一旦认领就必须完成，
                // 占不到 Restriction 时它还没被碰过。
                KeyAdmission? admission = restrictions.TryAdmit(key, upstreamModel);
                if (admission is null)
                {
                    keysRefused++;
                    keyBlock = restrictions.CheckKey(key, upstreamModel) ?? (BlockReason.Concurrency, null);
                    if (key.Allows(upstreamModel))
                    {
                        skipped.Add($"渠道 {channel.ChannelName}：渠道密钥 {key.Prefix}… 的并发或每分钟请求数已满");
                    }

                    continue;
                }

                // 占位：四个作用域都没被挡住才发；到期的作用域由这次尝试去半开探测（Q29）。
                // Workbench 只看不占：冷却中的按不可用处理，也不拿走到期后那一个探测名额（Q210）。
                CooldownPermit? permit = null;
                CooldownBlock? block;
                if (token is not null)
                {
                    permit = cooldowns.TryAcquire(channel.ChannelId, upstreamModel, key.Fingerprint, out block);
                }
                else
                {
                    block = cooldowns.CheckCandidate(channel.ChannelId, upstreamModel)
                        ?? cooldowns.CheckKey(key.Fingerprint, upstreamModel);
                }

                if (token is not null ? permit is null : block is not null)
                {
                    admission.Cancel();

                    // 挡在密钥维度就换这个渠道的下一把密钥，挡在渠道维度则整个候选跳过。
                    if (block is { IsKeyScoped: true })
                    {
                        continue;
                    }

                    break;
                }

                bool captureDebug = token is null
                    || Debugging.DebugSwitch.ShouldCapture(snapshot, token, channel, timeProvider.GetUtcNow());
                (AttemptRecord attempt, ForwardOutcome outcome) = await SendAttemptAsync(
                    context, dialect, snapshot, record, request, tracked, committer, channel, key, requestUri,
                    upstreamModel, sendPlan, body, captureDebug, presentedSecret, admission, stop).ConfigureAwait(false);
                last = outcome;

                // Workbench 的结果不写 Cooldown 也不写 Health（Q210）。
                if (permit is not null)
                {
                    DateTimeOffset? until = cooldowns.Complete(
                        permit,
                        channel.ChannelId,
                        upstreamModel,
                        key.Fingerprint,
                        outcome.FailureClass,
                        outcome.UpstreamResetAt);

                    attempt.CooldownScope = CooldownPolicy.ScopeOf(outcome.FailureClass);
                    attempt.CooldownUntil = until;
                    health.Record(channel.ChannelId, upstreamModel, outcome.FailureClass);
                }

                tracked.AttemptFinished(attempt);

                if (!thinkingRetried && ThinkingStyleMemory.RejectsThinking(sendPlan, outcome))
                {
                    thinkingRetried = true;
                    retryingThinking = true;
                    sendPlan = RetryThinking(channel, upstreamModel, record, request, sendPlan);
                    k--;
                    continue;
                }

                // 密钥级失败换同渠道的下一把密钥；其余失败整个渠道这一轮都不用再试了（Q28）。
                if (!IsKeyScoped(outcome.FailureClass))
                {
                    break;
                }
            }

            if (keysTried > 0 && keysRefused == keysTried && keyBlock is { } refused)
            {
                record.Skip(channel.ChannelId, channel.ChannelName, upstreamModel, refused.Reason, refused.RecoversAt);
            }
        }

        RecordAffinity(record, conversation, pin);

        if (tracked.InterruptRequested && !aborted.IsCancellationRequested && last?.Succeeded != true)
        {
            await WriteInterruptedAsync(record, dialect, committer, response, tracked.InterruptReason, aborted).ConfigureAwait(false);
            return;
        }

        if (landing is not null && last is null)
        {
            // 落点那把渠道密钥换了值、被停用，或者这一刻占不到：同样不换候选（ADR-0016）。
            string why = skipped.Count > 0
                ? landingLabel + " 的落点现在用不了：" + string.Join("；", skipped.Distinct())
                : landingLabel + " 的落点那把渠道密钥现在用不了（停用、换了值或在冷却）";
            await RejectStaleResponseAsync(record, response, dialect, landingLabel, why, aborted).ConfigureAwait(false);
            return;
        }

        if (last is null && skipped.Count > 0)
        {
            record.BlockedBy = FailureClass.RestrictionBlocked;
            record.BlockDetail = Truncate(string.Join("；", skipped.Distinct()));
            record.HttpStatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteErrorAsync(
                response,
                dialect,
                StatusCodes.Status503ServiceUnavailable,
                GatewayErrorKind.NoChannel,
                $"模型 {route.Name} 的候选渠道现在都受限制。",
                aborted).ConfigureAwait(false);
            return;
        }

        await FinishResponseAsync(record, dialect, committer, response, last, aborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 发一次 Attempt：建记录、按需录原文、转发、记用量与费用、记 Responses 的落点。
    /// Cooldown、Health 和换不换候选由调用方决定。<paramref name="admission"/> 在转发结束时归还。
    /// </summary>
    private async Task<(AttemptRecord Attempt, ForwardOutcome Outcome)> SendAttemptAsync(
        HttpContext context,
        InboundDialect dialect,
        ConfigSnapshot snapshot,
        RequestRecord record,
        InboundRequest request,
        InFlightRequest tracked,
        ResponseCommitter committer,
        ChannelSnapshot channel,
        ChannelKeySnapshot key,
        Uri requestUri,
        string upstreamModel,
        UpstreamPlan sendPlan,
        byte[] body,
        bool captureDebug,
        string? presentedSecret,
        IDisposable? admission,
        CancellationToken stop)
    {
        CancellationToken aborted = context.RequestAborted;

        // 换候选：上一轮缓冲的序幕作废。
        committer.DiscardPreamble();

        AttemptRecord attempt = StartAttempt(record, dialect, channel, key, requestUri, upstreamModel);
        tracked.AttemptStarted(attempt);

        // 原文只在开着开关时录，并且只有真的发出了 Attempt 才有（Q161）；Workbench 的请求一律录（Q213）。
        Debugging.AttemptDebugCapture? capture = null;
        if (captureDebug)
        {
            capture = new Debugging.AttemptDebugCapture(record.Id, attempt.Ordinal, attempt.StartedAt)
            {
                Secrets = [key.Secret, .. presentedSecret is null ? Array.Empty<string>() : [presentedSecret]],
            };

            // 客户端原文每个 Attempt 都带一份：一个 Attempt 的三份原文要能单独看懂（Q156）。
            capture.CaptureClientRequest(ClientHeaders(context.Request), body);
        }

        ForwardOutcome outcome;
        using (admission)
        {
            outcome = await ForwardOnceAsync(
                new ForwardRequest
                {
                    RequestUri = requestUri,
                    Proxy = channel.Proxy,
                    Rewriter = channel.Rewriter,
                    Secret = key.Secret,
                    UpstreamApiFormat = channel.ApiFormat,
                    AnthropicBearerAuth = channel.AnthropicBearerAuth,
                    InboundApiFormat = dialect.Format,
                    RequestKind = dialect.Kind,
                    ForwardedHeaders = ForwardedAnthropicHeaders(context.Request, dialect.Format, channel.ApiFormat),
                    Body = sendPlan.Body,
                    IsStream = request.IsStream,
                    SwallowUsageOnlyFrames = sendPlan.SwallowUsageOnlyFrames,
                    Conversion = sendPlan.Conversion,
                    Debug = capture,
                },
                committer,
                stop).ConfigureAwait(false);
        }

        if (capture is not null)
        {
            foreach (Debugging.DebugPayload payload in capture.Finish())
            {
                debugSink.Enqueue(payload);
            }
        }

        // 令牌被取消而客户端还在：是 Admin 中断的，不是客户端断开。
        if (outcome.FailureClass == FailureClass.ClientDisconnected
            && tracked.InterruptRequested
            && !aborted.IsCancellationRequested)
        {
            outcome.FailureClass = FailureClass.Interrupted;
        }

        CompleteAttempt(attempt, outcome, snapshot, channel, sendPlan.Body.Span, dialect.Kind, request.IsBackground);

        // 客户端拿到的是上游原样的响应 id（同格式透传）：记下落点，下一轮的 previous_response_id、
        // 按 id 取回 / 删除 / 取消才找得回来（ADR-0016、ADR-0020）。background 先回的 queued 也记，
        // 用量要等之后查到完成的状态再回填（Q277）。
        if (outcome.ResponseId is { Length: > 0 } responseId
            && (outcome.Succeeded || outcome.Committed)
            && dialect.RemembersResponseLandings
            && channel.ApiFormat == ApiFormat.OpenAiResponses)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            responseLandings.Remember(new ResponseLanding
            {
                Id = responseId,
                Kind = ResponseLandingKind.Response,
                ChannelId = channel.ChannelId,
                KeyFingerprint = key.Fingerprint,
                UpstreamModel = upstreamModel,
                ApiKeyId = record.ApiKeyId ?? 0,
                PublishedModel = record.PublishedModel,
                RequestId = record.Id,
                AwaitingUsage = request.IsBackground && !outcome.Usage.HasValue,
                CreatedAt = now,
                LastUsedAt = now,
            });
        }

        if (outcome.FailureClass == FailureClass.Interrupted)
        {
            attempt.ErrorMessage ??= "被 Admin 中断";
        }

        return (attempt, outcome);
    }

    /// <summary>
    /// Workbench 固定到一个渠道（Q200、Q209、Q210）：不选路，上游模型就是请求里的 model。
    /// 不受渠道和渠道密钥的 Restriction 约束（费用照记），冷却中也照发；
    /// 成功时清掉这条路上的四层冷却，失败什么都不写，也不碰 Health。
    /// 只在密钥级失败时换下一把密钥。
    /// </summary>
    private async Task RelayPinnedAsync(
        HttpContext context,
        InboundDialect dialect,
        ConfigSnapshot snapshot,
        RequestRecord record,
        InboundRequest request,
        ChannelSnapshot channel,
        InFlightRequest tracked,
        byte[] body)
    {
        HttpResponse response = context.Response;
        CancellationToken aborted = context.RequestAborted;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(aborted, tracked.InterruptToken);
        CancellationToken stop = stopping.Token;
        string upstreamModel = request.Model;

        if (!TryBuildUpstreamUri(channel, dialect, out Uri? requestUri))
        {
            string reason = dialect.Kind == RequestKind.Chat || !request.CanBeServedBy(channel.ApiFormat, out _)
                ? "渠道地址配置无效。"
                : UnderivablePath(dialect.Kind);
            record.HttpStatusCode = StatusCodes.Status502BadGateway;
            record.BlockDetail = $"渠道 {channel.ChannelName}：{reason.TrimEnd('。')}";
            await WriteErrorAsync(
                response, dialect, StatusCodes.Status502BadGateway, GatewayErrorKind.BadChannelConfig,
                reason, aborted).ConfigureAwait(false);
            return;
        }

        if (!request.CanBeServedBy(channel.ApiFormat, out string? unservable))
        {
            record.HttpStatusCode = StatusCodes.Status400BadRequest;
            record.BlockedBy = FailureClass.ClientRequestError;
            record.BlockDetail = Truncate($"渠道 {channel.ChannelName}：{unservable}");
            await WriteErrorAsync(
                response, dialect, StatusCodes.Status400BadRequest, GatewayErrorKind.InvalidRequest,
                unservable ?? "这个渠道接不了这个请求。", aborted).ConfigureAwait(false);
            return;
        }

        if (channel.Keys.Count == 0)
        {
            record.HttpStatusCode = StatusCodes.Status503ServiceUnavailable;
            record.BlockDetail = $"渠道 {channel.ChannelName} 没有可用的渠道密钥";
            await WriteErrorAsync(
                response, dialect, StatusCodes.Status503ServiceUnavailable, GatewayErrorKind.NoChannel,
                $"渠道 {channel.ChannelName} 没有可用的渠道密钥（都停用了、解不开，或者代理配置有误）。", aborted).ConfigureAwait(false);
            return;
        }

        UpstreamPlan sendPlan = request.PlanFor(channel, upstreamModel, record.Id, thinkingStyles.Find(channel, upstreamModel));
        using var committer = new ResponseCommitter(response, timeProvider);
        committer.OnCommitted = () =>
            tracked.Committed(record.Attempts.Count, timeProvider.GetUtcNow());
        ForwardOutcome? last = null;

        List<ChannelKeySnapshot> keys = [.. channel.Keys
            .OrderByDescending(k => k.Priority)
            .ThenBy(k => k.ChannelKeyId)];
        bool thinkingRetried = false;
        bool retryingThinking = false;
        for (int k = 0; k < keys.Count; k++)
        {
            ChannelKeySnapshot key = keys[k];
            bool retry = retryingThinking;
            retryingThinking = false;
            if (record.Attempts.Count >= MaxAttempts || (!retry && !ShouldTryAnother(last, dialect.Kind)) || tracked.InterruptRequested)
            {
                break;
            }

            (AttemptRecord attempt, ForwardOutcome outcome) = await SendAttemptAsync(
                context, dialect, snapshot, record, request, tracked, committer, channel, key, requestUri,
                upstreamModel, sendPlan, body, captureDebug: true, presentedSecret: null, admission: null, stop).ConfigureAwait(false);
            last = outcome;

            // 测通了就说明这条路此刻是好的：「充值或换了密钥之后点一下就恢复」（Q210）。
            if (outcome.FailureClass == FailureClass.None)
            {
                cooldowns.Clear(channel.ChannelId, upstreamModel, key.Fingerprint);
            }

            tracked.AttemptFinished(attempt);

            if (!thinkingRetried && ThinkingStyleMemory.RejectsThinking(sendPlan, outcome))
            {
                thinkingRetried = true;
                retryingThinking = true;
                sendPlan = RetryThinking(channel, upstreamModel, record, request, sendPlan);
                k--;
                continue;
            }

            if (!IsKeyScoped(outcome.FailureClass))
            {
                break;
            }
        }

        if (tracked.InterruptRequested && !aborted.IsCancellationRequested && last?.Succeeded != true)
        {
            await WriteInterruptedAsync(record, dialect, committer, response, tracked.InterruptReason, aborted).ConfigureAwait(false);
            return;
        }

        await FinishResponseAsync(record, dialect, committer, response, last, aborted).ConfigureAwait(false);
    }

    /// <summary>一次都没尝试就拒绝：落记录（零 Attempt、算失败），不进 Cooldown、不计健康度（Q85）。</summary>
    /// <summary>
    /// 落点和 Affinity 的关系（Q122），落点成功就记住它（Q119：整个渠道不可用时换了别处，也记新的落点）。
    /// 落点 = 成功或已提交的那次 Attempt；都没有时是最后一次。
    /// </summary>
    private void RecordAffinity(RequestRecord record, UInt128? conversation, AffinityPin? pin)
    {
        if (conversation is not { } key || record.Attempts.Count == 0)
        {
            return;
        }

        AttemptRecord landing = record.Attempts.LastOrDefault(a => a.FailureClass == FailureClass.None || a.Committed)
            ?? record.Attempts[^1];

        bool samePlace = pin is not null
            && landing.ChannelId == pin.ChannelId
            && landing.UpstreamModel == pin.UpstreamModel
            && landing.ChannelKeyFingerprint == pin.KeyFingerprint;
        record.Affinity = pin is null ? AffinityOutcome.New : samePlace ? AffinityOutcome.Kept : AffinityOutcome.Moved;

        if (landing.FailureClass == FailureClass.None || landing.Committed)
        {
            affinity.Remember(key, new AffinityPin(landing.ChannelId, landing.UpstreamModel, landing.ChannelKeyFingerprint));
        }
    }

    /// <summary>
    /// Admin 中断后告诉客户端（Q116）：提交前是 403（SDK 会自动重试 408/409/429/5xx），
    /// 已经发了响应头或已提交，就只能是流内错误事件。不换候选。
    /// </summary>
    private static async Task WriteInterruptedAsync(
        RequestRecord record,
        InboundDialect dialect,
        ResponseCommitter committer,
        HttpResponse response,
        InterruptReason reason,
        CancellationToken aborted)
    {
        bool shutdown = reason == InterruptReason.Shutdown;
        GatewayErrorKind kind = shutdown ? GatewayErrorKind.ShuttingDown : GatewayErrorKind.Interrupted;
        string message = shutdown ? "网关正在关闭，这个请求被中断了，请重试。" : "这个请求被管理员中断了。";
        if (record.Attempts.Count == 0)
        {
            record.BlockedBy = FailureClass.Interrupted;
            record.BlockDetail = shutdown ? "网关关闭时还没发出上游请求" : "发出上游请求之前被中断";
        }
        else if (shutdown)
        {
            record.BlockDetail = "网关关闭时被中断";
        }

        if (committer.Committed)
        {
            byte[] frame = dialect.ErrorFrame(kind, message);
            await TryWriteAsync(committer.WriteCommittedAsync(frame, aborted)).ConfigureAwait(false);
            record.HttpStatusCode = response.StatusCode;
            return;
        }

        if (committer.HeadersSent)
        {
            committer.DiscardPreamble();
            committer.BufferPreamble(dialect.ErrorFrame(kind, message));
            await TryWriteAsync(committer.FlushUncommittedAsync(aborted)).ConfigureAwait(false);
            record.HttpStatusCode = response.StatusCode;
            return;
        }

        // 停机切掉的请求客户端可以重试，回 503；Admin 中断的回 403（Q116）。
        int status = shutdown ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden;
        record.HttpStatusCode = status;
        await TryWriteAsync(WriteErrorAsync(response, dialect, status, kind, message, aborted)).ConfigureAwait(false);
    }

    private static async Task RejectAsync(
        RequestRecord record,
        HttpResponse response,
        InboundDialect dialect,
        int status,
        GatewayErrorKind kind,
        FailureClass blockedBy,
        string detail,
        string message,
        CancellationToken aborted)
    {
        record.HttpStatusCode = status;
        record.BlockedBy = blockedBy;
        record.BlockDetail = Truncate(detail);
        await WriteErrorAsync(response, dialect, status, kind, message, aborted).ConfigureAwait(false);
    }

    /// <summary>
    /// previous_response_id 或 conversation 回不到它的落点：400，让客户端重发完整上下文或者重建对话（ADR-0016、Q278）。不换候选。
    /// </summary>
    private static Task RejectStaleResponseAsync(
        RequestRecord record,
        HttpResponse response,
        InboundDialect dialect,
        string label,
        string detail,
        CancellationToken aborted) =>
        RejectAsync(
            record,
            response,
            dialect,
            StatusCodes.Status400BadRequest,
            GatewayErrorKind.InvalidRequest,
            FailureClass.ClientRequestError,
            detail,
            label == "conversation"
                ? "这个 conversation 在本网关上已经失效，请重新建一个对话。"
                : "这个 previous_response_id 在本网关上已经失效，请重发完整上下文。",
            aborted);

    /// <summary>
    /// 这个候选的上游地址：渠道存的是对话路径，别的请求从它推导（Q224）；compact 再接上 <c>/compact</c>（Q283）。
    /// </summary>
    private static bool TryBuildUpstreamUri(
        ChannelSnapshot channel,
        InboundDialect dialect,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? requestUri)
    {
        if (!UpstreamAddress.TryBuildRequestUri(channel.BaseUrl, channel.Path, channel.ApiFormat, dialect.Kind, out requestUri))
        {
            return false;
        }

        if (dialect.UpstreamPathSuffix is { } suffix)
        {
            var builder = new UriBuilder(requestUri!);
            builder.Path = builder.Path.TrimEnd('/') + suffix;
            requestUri = builder.Uri;
        }

        return true;
    }

    /// <summary>零候选时给面板看的原因：按渠道分组，例如「渠道 A：今日费用已达上限；渠道 B：不在可用时段」（Q93）。</summary>
    private static string DescribeBlocks(CandidatePlan plan, bool excludedByApiKey)
    {
        if (plan.Blocks.Count == 0)
        {
            return excludedByApiKey ? "API 密钥的渠道白名单 / 黑名单排除了所有候选" : "没有候选渠道";
        }

        return Truncate(string.Join(
            "；",
            plan.Blocks
                .GroupBy(b => b.Candidate.Channel.ChannelName, StringComparer.Ordinal)
                .Select(g => $"渠道 {g.Key}：{string.Join("、", g.Select(b => RestrictionChecker.Describe(b.Reason)).Distinct())}")));
    }

    private static string Truncate(string text) =>
        text.Length <= RequestRecord.MaxBlockDetailLength ? text : text[..(RequestRecord.MaxBlockDetailLength - 1)] + "…";

    private int SecondsUntil(DateTimeOffset at) =>
        Math.Max(1, (int)Math.Ceiling((at - timeProvider.GetUtcNow()).TotalSeconds));

    private async Task<ForwardOutcome> ForwardOnceAsync(
        ForwardRequest request,
        ResponseCommitter committer,
        CancellationToken aborted)
    {
        try
        {
            return await forwarder.ForwardAsync(request, committer, aborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return new ForwardOutcome { Committed = committer.Committed, FailureClass = FailureClass.ClientDisconnected };
        }
        catch (IOException) when (aborted.IsCancellationRequested)
        {
            // 写客户端时对方已经走了。
            return new ForwardOutcome { Committed = committer.Committed, FailureClass = FailureClass.ClientDisconnected };
        }
    }

    /// <summary>所有尝试结束后，按最后一次的结果决定客户端看到什么。</summary>
    private static async Task FinishResponseAsync(
        RequestRecord record,
        InboundDialect dialect,
        ResponseCommitter committer,
        HttpResponse response,
        ForwardOutcome? last,
        CancellationToken aborted)
    {
        if (last is null)
        {
            // 有渠道但一个地址都拼不出来：配置错了。
            record.HttpStatusCode = StatusCodes.Status502BadGateway;
            await WriteErrorAsync(
                response, dialect, StatusCodes.Status502BadGateway, GatewayErrorKind.BadChannelConfig, "渠道地址配置无效。", aborted)
                .ConfigureAwait(false);
            return;
        }

        if (last.Succeeded || last.FailureClass == FailureClass.ClientDisconnected)
        {
            record.HttpStatusCode = response.HasStarted ? response.StatusCode : last.HttpStatusCode;
            return;
        }

        if (committer.Committed)
        {
            // 提交之后才失败：不能重试，只能在流里告诉客户端。转发器已经告知过的（透传时上游自己的错误帧、
            // 转换时翻译出的错误事件）不再补；断流和空闲超时则要补，不然客户端只看到一个突然结束的流。
            if (!last.ClientNotified
                && record.IsStream
                && last.FailureClass is FailureClass.StreamInterrupted
                    or FailureClass.ConnectionFailure
                    or FailureClass.IdleTimeout)
            {
                byte[] frame = dialect.ErrorFrame(GatewayErrorKind.UpstreamInterrupted, last.ErrorMessage ?? "上游中途断开。");
                await TryWriteAsync(committer.WriteCommittedAsync(frame, aborted)).ConfigureAwait(false);
            }

            record.HttpStatusCode = response.StatusCode;
            return;
        }

        if (last.EndedEmpty)
        {
            // 每个候选都干净地结束却没出内容：把最后一个上游的（转换后的）原样交出去，不替它编错误。
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "text/event-stream";
            }

            await TryWriteAsync(committer.FlushUncommittedAsync(aborted)).ConfigureAwait(false);
            record.HttpStatusCode = response.StatusCode;
            return;
        }

        (int status, GatewayErrorKind kind, string message) = Describe(last);

        if (committer.HeadersSent)
        {
            // 已经按十秒上限发了 200：状态码改不了，只能是流内错误事件（ADR-0008 的代价）。
            committer.DiscardPreamble();
            committer.BufferPreamble(dialect.ErrorFrame(kind, message));
            await TryWriteAsync(committer.FlushUncommittedAsync(aborted)).ConfigureAwait(false);
            record.HttpStatusCode = response.StatusCode;
            return;
        }

        record.HttpStatusCode = status;

        if (last.FailureClass == FailureClass.ClientRequestError && last.ErrorBody is not null)
        {
            // 请求本身有问题，换候选也没用（Q28）。同格式时上游的错误原样还给客户端；
            // 转换时上游是另一种格式，取出消息按客户端的格式重新包装。
            // 只有这次尝试是同格式透传时，上游的错误体客户端才认得；入站 Chat 转给 Anthropic 时也不能照搬。
            bool passthrough = record.Attempts.Count > 0 && record.Attempts[^1].UpstreamApiFormat == dialect.Format;
            if (dialect.EchoesUpstreamClientErrors && passthrough)
            {
                response.StatusCode = status;
                response.ContentType = last.ErrorContentType ?? "application/json";
                await TryWriteAsync(response.WriteAsync(last.ErrorBody, aborted)).ConfigureAwait(false);
                return;
            }

            message = UpstreamErrorMessage(last.ErrorBody) ?? "上游拒绝了这个请求。";
        }

        await TryWriteAsync(WriteErrorAsync(response, dialect, status, kind, message, aborted)).ConfigureAwait(false);
    }

    /// <summary>
    /// 网关给客户端的错误。上游认证、额度这类细节不透出去——那是网关自己的凭证问题，客户端无能为力。
    /// </summary>
    private static (int Status, GatewayErrorKind Kind, string Message) Describe(ForwardOutcome last) =>
        last.FailureClass switch
        {
            FailureClass.ClientRequestError => (
                last.HttpStatusCode ?? StatusCodes.Status400BadRequest,
                GatewayErrorKind.InvalidRequest,
                last.ErrorMessage ?? "请求无效。"),
            FailureClass.RateLimited => (
                StatusCodes.Status429TooManyRequests,
                GatewayErrorKind.RateLimited,
                "所有候选渠道都被限流。"),
            FailureClass.FirstEventTimeout or FailureClass.IdleTimeout => (
                StatusCodes.Status504GatewayTimeout,
                GatewayErrorKind.Timeout,
                "上游响应超时。"),
            _ => (
                StatusCodes.Status502BadGateway,
                GatewayErrorKind.UpstreamFailed,
                "所有候选渠道都失败了。"),
        };

    /// <summary>从 OpenAI 形状的错误体里取 <c>error.message</c>；取不到返回 null。</summary>
    private static string? UpstreamErrorMessage(string errorBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(errorBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static async Task WriteErrorAsync(
        HttpResponse response,
        InboundDialect dialect,
        int status,
        GatewayErrorKind kind,
        string message,
        CancellationToken cancellationToken)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.Body.WriteAsync(dialect.ErrorBody(kind, message), cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端已经走了，没有人可写。
        }
        catch (IOException)
        {
        }
    }

    /// <summary>上一次的结果是否允许再试一个候选：提交点之前、而且是换候选可能有用的失败。</summary>
    private static bool ShouldTryAnother(ForwardOutcome? last, RequestKind kind) =>
        last is null
        || (!last.Committed
            && last.FailureClass is not (FailureClass.None
                or FailureClass.ClientRequestError
                or FailureClass.ClientDisconnected
                or FailureClass.Interrupted)

            // 图片请求超时时上游可能已经出图扣费了：换候选等于一张图付两次钱，直接报错（Q234）。
            && !(kind == RequestKind.Image
                && last.FailureClass is FailureClass.FirstEventTimeout or FailureClass.IdleTimeout));

    private static string KindName(RequestKind kind) => kind switch
    {
        RequestKind.Embedding => "嵌入",
        RequestKind.Image => "图片",
        _ => "对话",
    };

    private static string EndpointOf(RequestKind kind) => kind switch
    {
        RequestKind.Embedding => "/v1/embeddings",
        RequestKind.Image => "/v1/images/generations",
        _ => "/v1/chat/completions、/v1/messages 或 /v1/responses",
    };

    private static string UnderivablePath(RequestKind kind) =>
        $"渠道配了自定义路径，推不出{KindName(kind)}端点（路径以 /chat/completions 结尾才能推）。";

    /// <summary>只跟这把密钥有关的失败：同渠道的下一把密钥还有希望（Q28）。</summary>
    /// <summary>记住上游接受的思考写法，换成它重新算发送计划（Q315）。</summary>
    private UpstreamPlan RetryThinking(ChannelSnapshot channel, string upstreamModel, RequestRecord record, InboundRequest request, UpstreamPlan rejected)
    {
        AnthropicThinkingStyle style = rejected.ThinkingFallback!.Value;
        thinkingStyles.Remember(channel.ChannelId, upstreamModel, style);
        LogThinkingStyleLearned(logger, channel.ChannelName, upstreamModel, style);
        return request.PlanFor(channel, upstreamModel, record.Id, style);
    }

    private static bool IsKeyScoped(FailureClass failure) =>
        failure is FailureClass.AuthFailure or FailureClass.QuotaExhausted or FailureClass.RateLimited;

    private AttemptRecord StartAttempt(
        RequestRecord record,
        InboundDialect dialect,
        ChannelSnapshot channel,
        ChannelKeySnapshot key,
        Uri requestUri,
        string upstreamModel)
    {
        var attempt = new AttemptRecord
        {
            RequestId = record.Id,
            Ordinal = record.Attempts.Count + 1,
            StartedAt = timeProvider.GetUtcNow(),
            ChannelId = channel.ChannelId,
            ChannelName = channel.ChannelName,
            ChannelKeyId = key.ChannelKeyId,
            ChannelKeyFingerprint = key.Fingerprint,
            ChannelKeyPrefix = key.Prefix,
            UpstreamApiFormat = channel.ApiFormat,
            UpstreamModel = upstreamModel,
            RequestUri = requestUri.GetLeftPart(UriPartial.Path),
            Passthrough = dialect.Format == channel.ApiFormat,
            MultiplierBasisPoints = channel.MultiplierBasisPoints,
        };

        record.Attempts.Add(attempt);
        return attempt;
    }

    private void CompleteAttempt(
        AttemptRecord attempt,
        ForwardOutcome outcome,
        ConfigSnapshot snapshot,
        ChannelSnapshot channel,
        ReadOnlySpan<byte> sentBody,
        RequestKind kind,
        bool background = false)
    {
        attempt.FinishedAt = timeProvider.GetUtcNow();
        attempt.ResponseId = outcome.ResponseId is { Length: > 0 and <= ResponseLanding.MaxIdLength } responseId ? responseId : null;
        attempt.ImageCount = outcome.ImageCount;
        attempt.HttpStatusCode = outcome.HttpStatusCode;
        attempt.FailureClass = outcome.FailureClass;
        attempt.ErrorMessage = outcome.ErrorMessage;
        attempt.FirstEventAt = outcome.FirstEventAt;
        attempt.FirstContentEventAt = outcome.FirstContentEventAt;
        attempt.Committed = outcome.Committed;
        attempt.SawThinking = outcome.SawThinking;

        // 上游给了用量就用上游的；没给而这次确实消耗了（成功、已 Commit，或断开时还在进行中），
        // 按字符估一份，免得断开就等于免费（Q46/Q47）。
        // 嵌入按 input 估；图片不估 Token，按张计价或者记「无单价」（Q229/Q230）。
        Usage usage = outcome.Usage;
        if (!usage.HasValue && Consumed(outcome) && kind == RequestKind.Embedding)
        {
            usage = UsageEstimator.EstimateEmbedding(sentBody);
        }
        // background 先回的 queued 还没干活，不估：用量等查到完成状态时回填（Q277）。
        else if (!usage.HasValue && Consumed(outcome) && kind == RequestKind.Chat && !background)
        {
            usage = UsageEstimator.Estimate(sentBody, outcome.OutputAsciiChars, outcome.OutputWideChars);

            // Anthropic 流在 message_start 就给了真实的输入用量：输入用真数，只估输出（Q177）。
            if (outcome.PartialUsage.HasValue)
            {
                usage = outcome.PartialUsage with
                {
                    OutputTokens = usage.OutputTokens,
                    Source = UsageSource.GatewayEstimated,
                };
            }
        }

        attempt.InputTokens = usage.InputTokens;
        attempt.CacheReadTokens = usage.CacheReadTokens;
        attempt.CacheWrite5mTokens = usage.CacheWrite5mTokens;
        attempt.CacheWrite1hTokens = usage.CacheWrite1hTokens;
        attempt.OutputTokens = usage.OutputTokens;
        attempt.UsageSource = usage.Source;

        // 费用只算在消耗了的那次上；被换掉的失败尝试不计费（Q46）。
        if ((usage.HasValue || attempt.ImageCount > 0) && Consumed(outcome))
        {
            CostCalculator.Apply(attempt, snapshot.FindPrice(channel.Provider, attempt.UpstreamModel));
        }
    }

    /// <summary>这次尝试是不是真的消耗了上游：成功、已经 Commit，或者客户端断开 / 被中断时还在进行中。</summary>
    private static bool Consumed(ForwardOutcome outcome) =>
        outcome.FailureClass is FailureClass.None
            or FailureClass.ClientDisconnected
            or FailureClass.Interrupted
        || outcome.Committed;

    /// <summary>汇总所有 Attempt，把 Request 交给写入器。失败的尝试也可能消耗了用量，一并记账。</summary>
    private void Finish(RequestRecord record, bool interrupted, CancellationToken aborted)
    {
        record.FinishedAt = timeProvider.GetUtcNow();
        record.AttemptCount = record.Attempts.Count;
        restrictions.Costs.Record(record);

        AttemptRecord? decisive = record.Attempts.LastOrDefault();
        record.CommittedAt = record.Attempts.FirstOrDefault(a => a.Committed)?.FirstContentEventAt;
        record.ClientDisconnected = aborted.IsCancellationRequested
            || decisive?.FailureClass == FailureClass.ClientDisconnected;

        record.Status = decisive?.FailureClass switch
        {
            FailureClass.None => RequestStatus.Succeeded,
            FailureClass.ClientDisconnected => RequestStatus.Canceled,
            FailureClass.Interrupted => RequestStatus.Aborted,
            _ when record.ClientDisconnected => RequestStatus.Canceled,
            _ when interrupted => RequestStatus.Aborted,
            _ => RequestStatus.Failed,
        };

        foreach (AttemptRecord attempt in record.Attempts)
        {
            if (attempt.CostNanoUsd is { } cost)
            {
                record.CostNanoUsd = (record.CostNanoUsd ?? 0) + cost;
            }

            record.InputTokens += attempt.InputTokens;
            record.CacheReadTokens += attempt.CacheReadTokens;
            record.CacheWrite5mTokens += attempt.CacheWrite5mTokens;
            record.CacheWrite1hTokens += attempt.CacheWrite1hTokens;
            record.OutputTokens += attempt.OutputTokens;
            record.ImageCount += attempt.ImageCount;
            if (attempt.UsageSource > record.UsageSource)
            {
                record.UsageSource = attempt.UsageSource;
            }
        }

        try
        {
            recorder.Enqueue(record);
        }
        catch (InvalidOperationException ex)
        {
            // 只会在停机后还有请求结束时发生。
            LogRecordDropped(logger, ex, record.Id);
        }
    }

    /// <summary>读有界的请求体。超过上限返回 null。换候选要重发同样的字节，所以必须整体读进内存。</summary>
    /// <summary>客户端的请求头，转成脱敏器认识的形状。</summary>
    /// <summary>
    /// 两头都是 Anthropic 时，客户端的 <c>anthropic-version</c> 与 <c>anthropic-beta</c> 照转（Q165）：
    /// beta 功能是客户端选的，网关不该替它关掉。转换时上游收到的是网关写的请求，只用默认版本、不带 beta。
    /// 渠道的请求头改写规则在这之后执行，可以覆盖或去掉它们。
    /// </summary>
    internal static List<KeyValuePair<string, string>> ForwardedAnthropicHeaders(
        HttpRequest request,
        ApiFormat inbound,
        ApiFormat upstream)
    {
        if (inbound != ApiFormat.Anthropic || upstream != ApiFormat.Anthropic)
        {
            return [];
        }

        List<KeyValuePair<string, string>> headers = [];
        foreach (string name in (string[])[ApiFormatAuth.AnthropicVersionHeader, "anthropic-beta"])
        {
            string value = request.Headers[name].ToString();
            if (value.Length > 0)
            {
                headers.Add(new(name, value));
            }
        }

        return headers;
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> ClientHeaders(HttpRequest request) =>
        request.Headers.Select(h =>
            new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Where(v => v is not null)!));

    internal static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        IHttpMaxRequestBodySizeFeature? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxRequestBodyBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.ToArray();
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "请求 {RequestId} 结束时记录队列已关闭，这条记录没有落盘。")]
    private static partial void LogRecordDropped(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "渠道 {Channel} 的上游模型 {UpstreamModel} 不接受原来的思考写法，改用 {Style} 重发，并记住。")]
    private static partial void LogThinkingStyleLearned(ILogger logger, string channel, string upstreamModel, AnthropicThinkingStyle style);
}
