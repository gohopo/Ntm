using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Data;
using Ntm.Gateway.Live;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Server.Admin;

/// <summary>请求列表的一行。<see cref="ChannelName"/> 和 <see cref="UpstreamModel"/> 来自最后一次 Attempt。</summary>
internal sealed record RequestRowView(
    Guid Id,
    DateTimeOffset StartedAt,
    long? DurationMs,
    long? TimeToFirstContentMs,
    string PublishedModel,
    string? ChannelName,
    string? UpstreamModel,
    string? ApiKeyName,
    RequestOrigin Origin,
    ApiFormat InboundApiFormat,
    bool IsStream,
    RequestStatus Status,
    int? HttpStatusCode,
    int AttemptCount,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    UsageSource UsageSource,
    long? CostNanoUsd,
    bool Unpriced,
    bool ClientDisconnected,
    FailureClass? BlockedBy,
    RequestKind RequestKind = RequestKind.Chat,
    int ImageCount = 0);

internal sealed record RequestPageView(IReadOnlyList<RequestRowView> Items, string? NextCursor);

internal sealed record AttemptView(
    int Ordinal,
    DateTimeOffset StartedAt,
    long? DurationMs,
    long? FirstEventMs,
    long? FirstContentMs,
    int ChannelId,
    string ChannelName,
    string ChannelKeyPrefix,
    string UpstreamModel,
    ApiFormat UpstreamApiFormat,
    string RequestUri,
    bool Passthrough,
    int? HttpStatusCode,
    FailureClass FailureClass,
    CooldownScope CooldownScope,
    DateTimeOffset? CooldownUntil,
    string? ErrorMessage,
    bool Committed,
    long InputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long OutputTokens,
    UsageSource UsageSource,
    long? CostNanoUsd,
    bool Unpriced,
    int MultiplierBasisPoints,
    long? LongContextOver,
    decimal? InputPrice,
    decimal? CacheReadPrice,
    decimal? CacheWrite5mPrice,
    decimal? CacheWrite1hPrice,
    decimal? OutputPrice,
    bool SawThinking,
    bool InProgress = false,
    int ImageCount = 0,
    decimal? ImagePrice = null,
    string? ResponseId = null);

internal sealed record RequestDetailView(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    long? TimeToFirstContentMs,
    string PublishedModel,
    int? ApiKeyId,
    string? ApiKeyName,
    RequestOrigin Origin,
    ApiFormat InboundApiFormat,
    RequestKind RequestKind,
    bool IsStream,
    RequestStatus Status,
    int? HttpStatusCode,
    DateTimeOffset? CommittedAt,
    bool ClientDisconnected,
    long InputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long OutputTokens,
    UsageSource UsageSource,
    long? CostNanoUsd,
    bool Unpriced,
    IReadOnlyList<AttemptView> Attempts,
    FailureClass? BlockedBy,
    string? BlockDetail,
    AffinityOutcome? Affinity,
    bool InterruptRequested = false,
    int ImageCount = 0,
    IReadOnlyList<SkippedCandidateView>? Skipped = null);

/// <summary>选路时被挡掉的候选（Q263）。</summary>
internal sealed record SkippedCandidateView(
    int ChannelId,
    string ChannelName,
    string UpstreamModel,
    BlockReason Reason,
    DateTimeOffset? RecoversAt);

/// <summary>/api/requests：请求日志。只读。</summary>
internal static class RequestEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>DateTimeOffset.MaxValue 对应的 Unix 毫秒数，游标里的时间不能超过它。</summary>
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/interrupt", Interrupt);
        return group;
    }

    /// <summary>
    /// 按开始时间倒序翻页。游标是 <c>{毫秒}.{跳过数}</c>：时间相同的若干行靠跳过数接上，
    /// 比单纯的 offset 稳——翻页途中新来的请求时间更晚，不会把后面的页挤歪。
    /// </summary>
    private static async Task<IResult> ListAsync(
        string? cursor,
        int? limit,
        string? model,
        string? status,
        int? apiKeyId,
        string? origin,
        string? kind,
        int? channelId,
        TrafficDbContext traffic,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        int take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            errors.Add("limit", "每页要在 1 到 200 条之间。");
        }

        (DateTimeOffset At, int Skip)? position = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (TryParseCursor(cursor, out DateTimeOffset at, out int skip))
            {
                position = (at, skip);
            }
            else
            {
                errors.Add("cursor", "翻页游标无效。");
            }
        }

        RequestStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (TryParseStatus(status, out RequestStatus parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", "没有这种状态。");
            }
        }

        // 来源筛选（Q208）：client 或 workbench，默认全部。
        RequestOrigin? originFilter = null;
        if (!string.IsNullOrEmpty(origin))
        {
            if (Enum.TryParse(origin, ignoreCase: true, out RequestOrigin parsedOrigin)
                && Enum.IsDefined(parsedOrigin)
                && !int.TryParse(origin, out _))
            {
                originFilter = parsedOrigin;
            }
            else
            {
                errors.Add("origin", "没有这种来源。");
            }
        }

        // 类型筛选（Q236）：chat、embedding、image，默认全部。
        RequestKind? kindFilter = null;
        if (!string.IsNullOrEmpty(kind))
        {
            if (Enum.TryParse(kind, ignoreCase: true, out RequestKind parsedKind)
                && Enum.IsDefined(parsedKind)
                && !int.TryParse(kind, out _))
            {
                kindFilter = parsedKind;
            }
            else
            {
                errors.Add("kind", "没有这种请求类型。");
            }
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        IQueryable<RequestRecord> query = traffic.Requests.AsNoTracking();
        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(r => r.PublishedModel == model);
        }

        if (statusFilter is RequestStatus wanted)
        {
            query = query.Where(r => r.Status == wanted);
        }

        if (apiKeyId is int token)
        {
            query = query.Where(r => r.ApiKeyId == token);
        }

        if (originFilter is RequestOrigin wantedOrigin)
        {
            query = query.Where(r => r.Origin == wantedOrigin);
        }

        if (kindFilter is RequestKind wantedKind)
        {
            query = query.Where(r => r.RequestKind == wantedKind);
        }

        // 有任何一次尝试落到这个渠道就算（Q264）。尝试表上有（渠道, 时间）索引。
        if (channelId is int wantedChannel)
        {
            query = query.Where(r => r.Attempts.Any(a => a.ChannelId == wantedChannel));
        }

        int offset = 0;
        if (position is (DateTimeOffset before, int previousSkip))
        {
            query = query.Where(r => r.StartedAt <= before);
            offset = previousSkip;
        }

        // 用两个标量子查询取最后一次 Attempt 的渠道和模型：SQLite 不支持 APPLY，投影成对象会翻译失败。
        var rows = await query
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Skip(offset)
            .Take(take + 1)
            .Select(r => new
            {
                Record = r,
                ChannelName = r.Attempts.OrderByDescending(a => a.Ordinal).Select(a => a.ChannelName).FirstOrDefault(),
                UpstreamModel = r.Attempts.OrderByDescending(a => a.Ordinal).Select(a => a.UpstreamModel).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            DateTimeOffset lastAt = rows[^1].Record.StartedAt;
            int ties = rows.Count(r => r.Record.StartedAt == lastAt);
            int nextSkip = position is (DateTimeOffset at, int skip) && at == lastAt ? skip + ties : ties;
            nextCursor = lastAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                + "." + nextSkip.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Ok(new RequestPageView(
            [.. rows.Select(r => ToRow(r.Record, r.ChannelName, r.UpstreamModel))],
            nextCursor));
    }

    /// <summary>
    /// 中断一个进行中的 Request（Q115）：202 表示已经发出中断，结果从实时链路或详情页看；
    /// 已经结束或不存在时 404。
    /// </summary>
    private static IResult Interrupt(Guid id, InFlightRegistry inFlight) =>
        inFlight.TryInterrupt(id) ? TypedResults.Accepted((string?)null) : AdminResults.NotFound("进行中的请求");

    private static async Task<IResult> GetAsync(
        Guid id,
        TrafficDbContext traffic,
        InFlightRegistry inFlight,
        CancellationToken cancellationToken)
    {
        // 还没结束的不在库里：从进行中的表拼一份（Q112）。结束后记录异步写盘，前端会稍后重取。
        if (inFlight.Find(id) is { } running)
        {
            return TypedResults.Ok(FromInFlight(running));
        }

        RequestRecord? record = await traffic.Requests
            .AsNoTracking()
            .Include(r => r.Attempts)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return AdminResults.NotFound("请求记录");
        }

        return TypedResults.Ok(new RequestDetailView(
            record.Id,
            record.StartedAt,
            record.FinishedAt,
            Milliseconds(record.StartedAt, record.FinishedAt),
            TimeToFirstContent(record),
            record.PublishedModel,
            record.ApiKeyId,
            record.ApiKeyName,
            record.Origin,
            record.InboundApiFormat,
            record.RequestKind,
            record.IsStream,
            record.Status,
            record.HttpStatusCode,
            record.CommittedAt,
            record.ClientDisconnected,
            record.InputTokens,
            record.CacheReadTokens,
            record.CacheWrite5mTokens,
            record.CacheWrite1hTokens,
            record.OutputTokens,
            record.UsageSource,
            record.CostNanoUsd,
            Unpriced(record),
            [.. record.Attempts.OrderBy(a => a.Ordinal).Select(ToAttempt)],
            record.BlockedBy,
            record.BlockDetail,
            record.Affinity,
            ImageCount: record.ImageCount,
            Skipped: [.. record.Skipped.Select(x => new SkippedCandidateView(
                x.ChannelId,
                x.ChannelName,
                x.UpstreamModel,
                x.Reason,
                x.RecoversAtMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null))]));
    }

    private static RequestDetailView FromInFlight(InFlightRequest running)
    {
        LiveRequest live = running.ToLive();
        return new RequestDetailView(
            live.RequestId,
            live.StartedAt,
            null,
            null,
            null,
            live.PublishedModel,
            live.ApiKeyId,
            live.ApiKeyName,
            live.Origin,
            running.InboundApiFormat,
            live.RequestKind,
            live.IsStream,
            RequestStatus.Running,
            null,
            live.CommittedAt,
            false,
            0,
            0,
            0,
            0,
            0,
            UsageSource.Unavailable,
            null,
            false,
            [.. live.Attempts.Select(a => ToAttempt(
                a,
                running.InboundApiFormat,
                committed: live.CommittedAt is not null && a.AttemptNumber == live.Attempts[^1].AttemptNumber))],
            null,
            null,
            null,
            live.InterruptRequested);
    }

    /// <summary>进行中的尝试：只有实时链路里那些字段，用量和单价要等结束写盘后才有。提交过的只可能是最后一次尝试。</summary>
    private static AttemptView ToAttempt(LiveAttempt attempt, ApiFormat inboundFormat, bool committed) =>
        new(
            attempt.AttemptNumber,
            attempt.StartedAt,
            Milliseconds(attempt.StartedAt, attempt.FinishedAt),
            null,
            null,
            attempt.ChannelId,
            attempt.ChannelName,
            attempt.KeyPrefix,
            attempt.UpstreamModel,
            attempt.UpstreamApiFormat,
            attempt.RequestUri,
            inboundFormat == attempt.UpstreamApiFormat,
            attempt.HttpStatusCode,
            attempt.FailureClass ?? FailureClass.None,
            attempt.CooldownScope ?? CooldownScope.None,
            attempt.CooldownUntil,
            attempt.ErrorMessage,
            committed,
            0,
            0,
            0,
            0,
            0,
            UsageSource.Unavailable,
            null,
            false,
            10000,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            attempt.FinishedAt is null);

    private static RequestRowView ToRow(RequestRecord record, string? channelName, string? upstreamModel) =>
        new(
            record.Id,
            record.StartedAt,
            Milliseconds(record.StartedAt, record.FinishedAt),
            TimeToFirstContent(record),
            record.PublishedModel,
            channelName,
            upstreamModel,
            record.ApiKeyName,
            record.Origin,
            record.InboundApiFormat,
            record.IsStream,
            record.Status,
            record.HttpStatusCode,
            record.AttemptCount,
            record.InputTokens,
            record.CacheReadTokens,
            record.CacheWrite5mTokens + record.CacheWrite1hTokens,
            record.OutputTokens,
            record.UsageSource,
            record.CostNanoUsd,
            Unpriced(record),
            record.ClientDisconnected,
            record.BlockedBy,
            record.RequestKind,
            record.ImageCount);

    private static AttemptView ToAttempt(AttemptRecord attempt) =>
        new(
            attempt.Ordinal,
            attempt.StartedAt,
            Milliseconds(attempt.StartedAt, attempt.FinishedAt),
            Milliseconds(attempt.StartedAt, attempt.FirstEventAt),
            Milliseconds(attempt.StartedAt, attempt.FirstContentEventAt),
            attempt.ChannelId,
            attempt.ChannelName,
            attempt.ChannelKeyPrefix,
            attempt.UpstreamModel,
            attempt.UpstreamApiFormat,
            attempt.RequestUri,
            attempt.Passthrough,
            attempt.HttpStatusCode,
            attempt.FailureClass,
            attempt.CooldownScope,
            attempt.CooldownUntil,
            attempt.ErrorMessage,
            attempt.Committed,
            attempt.InputTokens,
            attempt.CacheReadTokens,
            attempt.CacheWrite5mTokens,
            attempt.CacheWrite1hTokens,
            attempt.OutputTokens,
            attempt.UsageSource,
            attempt.CostNanoUsd,
            attempt.CostNanoUsd is null && (Tokens(attempt) > 0 || attempt.ImageCount > 0),
            attempt.MultiplierBasisPoints,
            attempt.LongContextOver,
            attempt.InputPrice,
            attempt.CacheReadPrice,
            attempt.CacheWrite5mPrice,
            attempt.CacheWrite1hPrice,
            attempt.OutputPrice,
            attempt.SawThinking,
            ImageCount: attempt.ImageCount,
            ImagePrice: attempt.ImagePrice,
            ResponseId: attempt.ResponseId);

    /// <summary>
    /// 「无单价」：用了 Token 或出了图却算不出费用（Q61/Q230）。费用为 null 而 Token 也是 0 的那种
    /// （上游都没碰到就失败了）不算无单价，页面上显示「—」。
    /// </summary>
    private static bool Unpriced(RequestRecord record) =>
        record.CostNanoUsd is null
        && (record.ImageCount > 0
            || record.InputTokens
                + record.CacheReadTokens
                + record.CacheWrite5mTokens
                + record.CacheWrite1hTokens
                + record.OutputTokens > 0);

    private static long Tokens(AttemptRecord attempt) =>
        attempt.InputTokens
        + attempt.CacheReadTokens
        + attempt.CacheWrite5mTokens
        + attempt.CacheWrite1hTokens
        + attempt.OutputTokens;

    /// <summary>首字时间：从请求开始到第一个携带真实输出的事件写给客户端（提交点，ADR-0008）。</summary>
    // 首字时间只对流式请求有意义：非流式的 CommittedAt 是整个响应体写完的时刻（Q58）。
    private static long? TimeToFirstContent(RequestRecord record) =>
        record.IsStream ? Milliseconds(record.StartedAt, record.CommittedAt) : null;

    private static long? Milliseconds(DateTimeOffset from, DateTimeOffset? to) =>
        to is DateTimeOffset end ? (long)(end - from).TotalMilliseconds : null;

    private static bool TryParseCursor(string text, out DateTimeOffset at, out int skip)
    {
        at = default;
        skip = 0;
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0
            || !long.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds)
            || milliseconds > MaxUnixMilliseconds
            || !int.TryParse(text.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out skip))
        {
            return false;
        }

        at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }

    /// <summary>只认枚举成员的名字（不分大小写），不认数字，也不认逗号拼起来的组合。</summary>
    private static bool TryParseStatus(string text, out RequestStatus status)
    {
        foreach (RequestStatus candidate in Enum.GetValues<RequestStatus>())
        {
            if (string.Equals(Enum.GetName(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }
}
