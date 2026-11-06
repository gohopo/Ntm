using Ntm.Protocol;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 客户端打进来的一次调用，从进入网关到给出最终结果。它可能包含多次 Attempt。
/// 落在 ntm-traffic.db，和汇总表同库同事务（ADR-0004）。
/// 渠道名、API 密钥名这类字段是反范式存的：跨库 join 不存在。
/// </summary>
public sealed class RequestRecord
{
    /// <summary>GUID v7，按时间有序。客户端在批量落库之前就需要能引用这条记录。</summary>
    public Guid Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int? ApiKeyId { get; set; }

    /// <summary>反范式。API 密钥在 ntm.db，这里只留名字。</summary>
    public string? ApiKeyName { get; set; }

    /// <summary>客户端还是 Workbench（Q208）。Workbench 的请求没有 API 密钥。</summary>
    public RequestOrigin Origin { get; set; }

    /// <summary>客户端请求的那个模型名。</summary>
    public required string PublishedModel { get; set; }

    public ApiFormat InboundApiFormat { get; set; }

    public RequestKind RequestKind { get; set; }

    public bool IsStream { get; set; }

    public RequestStatus Status { get; set; }

    /// <summary>最终返回给客户端的 HTTP 状态码。</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>第一个 ContentEvent 写给客户端的时刻，即 Commit 时刻（ADR-0008）。</summary>
    public DateTimeOffset? CommittedAt { get; set; }

    public int AttemptCount { get; set; }

    public bool ClientDisconnected { get; set; }

    public long InputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWrite5mTokens { get; set; }

    public long CacheWrite1hTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>用量来自上游精确值还是网关估算（Q20）。</summary>
    public UsageSource UsageSource { get; set; }

    /// <summary>纳美元（ADR-0009），已乘渠道倍率（Q34）。缺单价时为 null，不是 0。</summary>
    public long? CostNanoUsd { get; set; }

    /// <summary>图片请求各次 Attempt 返回的张数之和（Q230）。别的请求是 0。</summary>
    public int ImageCount { get; set; }

    /// <summary>
    /// 一次都没尝试就结束时，为什么（Q85/Q93）：<see cref="FailureClass.QuotaBlocked"/>、<see cref="FailureClass.RestrictionBlocked"/>，
    /// 或者其他零尝试的结局（没下发、没有渠道、都在冷却）时为 null，原因只写在 <see cref="BlockDetail"/> 里。
    /// </summary>
    public FailureClass? BlockedBy { get; set; }

    /// <summary>零尝试时给人看的原因，例如「渠道 A：今日费用已达上限；渠道 B：不在可用时段」。最长 <see cref="MaxBlockDetailLength"/> 字。</summary>
    public string? BlockDetail { get; set; }

    /// <summary>和 Affinity 的关系（Q122）。没算出 Conversation 或一次 Attempt 都没有时为 null。</summary>
    public AffinityOutcome? Affinity { get; set; }

    public const int MaxBlockDetailLength = 1000;

    public const int MaxSkippedCandidates = 10;

    /// <summary>选路时被冷却或 Restriction 挡掉的候选（Q263）。平时是空的。</summary>
    public List<SkippedCandidate> Skipped { get; } = [];

    /// <summary>记下一个被挡掉的候选；同一个候选只记一次，超过上限的丢掉。</summary>
    public void Skip(int channelId, string channelName, string upstreamModel, Restrictions.BlockReason reason, DateTimeOffset? recoversAt)
    {
        if (Skipped.Count >= MaxSkippedCandidates
            || Skipped.Exists(s => s.ChannelId == channelId && s.UpstreamModel == upstreamModel))
        {
            return;
        }

        Skipped.Add(new SkippedCandidate
        {
            ChannelId = channelId,
            ChannelName = channelName,
            UpstreamModel = upstreamModel,
            Reason = reason,
            RecoversAtMs = recoversAt?.ToUnixTimeMilliseconds(),
        });
    }

    public List<AttemptRecord> Attempts { get; } = [];
}
