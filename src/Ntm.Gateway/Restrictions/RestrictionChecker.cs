using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Restrictions;

/// <summary>候选为什么现在用不了。冷却和 Restriction 分开：前者是上游出错，后者是网关自己的配置（GLOSSARY: Restriction）。</summary>
public enum BlockReason
{
    Cooldown,
    DailyCostLimit,
    OutsideTimeWindow,
    RequestsPerMinute,
    Concurrency,

    /// <summary>没有一把渠道密钥的模型白名单含这个上游模型。这是配置如此，不会自己恢复。</summary>
    KeyModelWhitelist,
}

/// <summary>一个被挡住的候选。<see cref="RecoversAt"/> 为 null 表示说不准什么时候恢复。</summary>
public sealed record CandidateBlock(RouteCandidate Candidate, BlockReason Reason, DateTimeOffset? RecoversAt);

/// <summary>
/// 渠道和渠道密钥上的 Restriction（Q79–Q82）。触发时候选或密钥退出选路，不进入 Cooldown，也不计健康度。
/// 选路时用它判断，中继在真正发出前再判断一次——两者之间状态可能变了。
/// </summary>
public sealed class RestrictionChecker(
    CostLedger spend,
    RequestRateCounter rates,
    ConcurrencyGauges concurrency,
    GatewayTimeZone timeZone,
    TimeProvider timeProvider)
{
    public CostLedger Costs => spend;

    public RequestRateCounter Rates => rates;

    public ConcurrencyGauges Concurrency => concurrency;

    public GatewayTimeZone TimeZone => timeZone;

    /// <summary>渠道级：每日费用、可用时段。挡住时给出原因和恢复时刻。</summary>
    public (BlockReason Reason, DateTimeOffset? RecoversAt)? CheckChannel(ChannelSnapshot channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.DailyCostLimitNanoUsd is { } limit && spend.ChannelToday(channel.ChannelId) >= limit)
        {
            // 软上限（Q107）：只在发出前比，不预扣、不在流中途掐断，所以实际花费可能略超上限。
            return (BlockReason.DailyCostLimit, timeZone.StartOf(timeZone.Today().AddDays(1)));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTime local = timeZone.ToLocal(now);
        if (!TimeWindowSchedule.IsOpen(channel.TimeWindows, local))
        {
            DateTime? next = TimeWindowSchedule.NextOpen(channel.TimeWindows, local);
            return (BlockReason.OutsideTimeWindow, next is { } at ? timeZone.ToUtc(at) : null);
        }

        return null;
    }

    /// <summary>密钥级：模型白名单、RPM、并发。只看，不占。</summary>
    public (BlockReason Reason, DateTimeOffset? RecoversAt)? CheckKey(ChannelKeySnapshot key, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!key.Allows(upstreamModel))
        {
            return (BlockReason.KeyModelWhitelist, null);
        }

        if (!rates.HasRoom(key.Fingerprint, key.RequestsPerMinute))
        {
            return (BlockReason.RequestsPerMinute, rates.NextMinute());
        }

        if (!concurrency.ChannelKeys.HasRoom(key.Fingerprint, key.MaxConcurrency))
        {
            return (BlockReason.Concurrency, null);
        }

        return null;
    }

    /// <summary>
    /// 真正发出前占位：并发租约 + 这一分钟的一次。任何一样占不到就全部退回，返回 null（Q92：抢输了就换下一把，不算失败）。
    /// </summary>
    public KeyAdmission? TryAdmit(ChannelKeySnapshot key, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!key.Allows(upstreamModel))
        {
            return null;
        }

        IDisposable? lease = concurrency.ChannelKeys.TryAcquire(key.Fingerprint, key.MaxConcurrency);
        if (lease is null)
        {
            return null;
        }

        if (!rates.TryTake(key.Fingerprint, key.RequestsPerMinute))
        {
            lease.Dispose();
            return null;
        }

        return new KeyAdmission(lease, () => rates.Refund(key.Fingerprint));
    }

    /// <summary>给人看的原因，面板上用（Q93）。</summary>
    public static string Describe(BlockReason reason) => reason switch
    {
        BlockReason.Cooldown => "冷却中",
        BlockReason.DailyCostLimit => "今日费用已达上限",
        BlockReason.OutsideTimeWindow => "不在可用时段",
        BlockReason.RequestsPerMinute => "渠道密钥的每分钟请求数已满",
        BlockReason.Concurrency => "渠道密钥的并发已满",
        BlockReason.KeyModelWhitelist => "没有渠道密钥允许这个上游模型",
        _ => reason.ToString(),
    };
}

/// <summary>一把渠道密钥的占位。这次 Attempt 结束时 Dispose 归还并发；没发出去时先 <see cref="Cancel"/> 退回 RPM。</summary>
public sealed class KeyAdmission(IDisposable lease, Action refund) : IDisposable
{
    private bool _canceled;

    /// <summary>占了却没发出去（比如被冷却挡住）：这一分钟的那一次也退回。</summary>
    public void Cancel()
    {
        if (!_canceled)
        {
            _canceled = true;
            refund();
        }

        lease.Dispose();
    }

    public void Dispose() => lease.Dispose();
}
