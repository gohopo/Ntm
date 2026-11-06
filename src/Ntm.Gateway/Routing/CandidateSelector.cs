using Ntm.Gateway.Configuration;
using Ntm.Gateway.Restrictions;

namespace Ntm.Gateway.Routing;

/// <summary>选路的结果：按尝试顺序排好的候选，以及被挡掉的候选各自的原因。</summary>
public sealed class CandidatePlan
{
    public required IReadOnlyList<RouteCandidate> Candidates { get; init; }

    /// <summary>被冷却或 Restriction 挡掉的候选。被过滤器排除的不在这里。</summary>
    public IReadOnlyList<CandidateBlock> Blocks { get; init; } = [];

    /// <summary>
    /// 没有候选时，最早什么时候可能恢复（Q91）：冷却到期、下一分钟、下一个 0 点、下一个可用时段，取最早。
    /// 有候选卡在并发上（说不准什么时候放开）、或者有候选可用时为 null。
    /// 渠道密钥模型白名单挡掉的不算：那不会自己恢复。
    /// </summary>
    public DateTimeOffset? EarliestRecovery { get; init; }

    /// <summary>一个候选都排不出来，而且挡住它们的全是冷却——该让客户端稍后再来，而不是报配置错误。</summary>
    public bool AllCooling =>
        Candidates.Count == 0
        && EarliestRecovery is not null
        && Blocks.All(b => b.Reason == BlockReason.Cooldown);

    /// <summary>至少有一个候选是被 Restriction（而不是冷却）挡住的。</summary>
    public bool HasRestriction => Blocks.Any(b => b.Reason != BlockReason.Cooldown);

    /// <summary>Affinity 记住的候选排在了第一位（Q119/Q120）。</summary>
    public bool PinApplied { get; init; }
}

/// <summary>
/// 按 Priority 分档，档内按健康度调整后的 Weight 做平滑加权轮询（Q38）。
/// 只有每档的头名由轮询选出，其余保持匹配给出的确定顺序——那就是故障转移的顺序。
/// 渠道的每日费用和可用时段挡住整个候选；渠道密钥的冷却、RPM、并发和模型白名单挡住单把密钥，
/// 一把都不剩时候选才退出（Q92）。Restriction 不进入 Cooldown。
/// 给了 Affinity 的落点时，只要它在当前最高的可用档里而且可用，就排第一、不动轮询；否则照常轮询（Q120）。
/// </summary>
public sealed class CandidateSelector(
    CooldownRegistry cooldowns,
    HealthTracker health,
    RestrictionChecker? restrictions = null)
{
    public CandidatePlan Plan(
        PublishedModelRoute route,
        Func<RouteCandidate, bool>? filter = null,
        AffinityPin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        List<RouteCandidate> ordered = [];
        List<CandidateBlock> blocks = [];
        bool pinApplied = false;

        foreach (PriorityGroup group in route.PriorityGroups)
        {
            List<RouteCandidate> usable = [];
            List<int> keys = [];
            List<int> weights = [];

            for (int i = 0; i < group.Candidates.Count; i++)
            {
                RouteCandidate candidate = group.Candidates[i];
                if (filter is not null && !filter(candidate))
                {
                    continue;
                }

                if (Check(candidate) is { } block)
                {
                    blocks.Add(block);
                    continue;
                }

                usable.Add(candidate);

                // 轮询状态按候选在档内的稳定下标记，和权重无关。
                keys.Add(i);
                weights.Add(health.EffectiveWeight(candidate.ChannelId, candidate.UpstreamModel, candidate.Weight));
            }

            if (usable.Count == 0)
            {
                continue;
            }

            // Affinity 只看第一个有可用候选的档：Priority 优先于 Affinity，主力恢复了就回主力（Q120）。
            // 命中时不推进轮询，不然别的 Conversation 的分布会被它带偏。
            int pinned = ordered.Count == 0 && pin is not null
                ? usable.FindIndex(c => c.ChannelId == pin.ChannelId && c.UpstreamModel == pin.UpstreamModel)
                : -1;
            if (pinned >= 0)
            {
                pinApplied = true;
                ordered.Add(usable[pinned]);
                ordered.AddRange(usable.Where((_, i) => i != pinned));
                continue;
            }

            int picked = group.RoundRobin.Pick(keys, weights);
            if (picked > 0)
            {
                ordered.Add(usable[picked]);
                for (int i = 0; i < usable.Count; i++)
                {
                    if (i != picked)
                    {
                        ordered.Add(usable[i]);
                    }
                }
            }
            else
            {
                ordered.AddRange(usable);
            }
        }

        return new CandidatePlan
        {
            Candidates = ordered,
            Blocks = blocks,
            EarliestRecovery = ordered.Count == 0 ? Earliest(blocks) : null,
            PinApplied = pinApplied,
        };
    }

    /// <summary>
    /// 每个候选此刻的状态，给后台的「现在的选路」看（Q259）。和 <see cref="Plan"/> 用同一套判断，
    /// 但只读：不推进轮询，也不认领冷却到期后的探测名额。冷却到期、还没人去探测的候选算可用。
    /// </summary>
    public IReadOnlyList<CandidateState> Inspect(PublishedModelRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        List<CandidateState> states = [];
        foreach (PriorityGroup group in route.PriorityGroups)
        {
            foreach (RouteCandidate candidate in group.Candidates)
            {
                states.Add(new CandidateState(
                    candidate,
                    Check(candidate),
                    health.Sample(candidate.ChannelId, candidate.UpstreamModel),
                    health.EffectiveWeight(candidate.ChannelId, candidate.UpstreamModel, candidate.Weight)));
            }
        }

        return states;
    }

    /// <summary>这个候选现在能不能用；不能时说明原因。</summary>
    private CandidateBlock? Check(RouteCandidate candidate)
    {
        if (restrictions?.CheckChannel(candidate.Channel) is { } channelBlock)
        {
            return new CandidateBlock(candidate, channelBlock.Reason, channelBlock.RecoversAt);
        }

        if (cooldowns.CheckCandidate(candidate.ChannelId, candidate.UpstreamModel) is { } cooling)
        {
            return new CandidateBlock(candidate, BlockReason.Cooldown, cooling.Until);
        }

        if (candidate.Channel.Keys.Count == 0)
        {
            return null;
        }

        // 一把能用的密钥就够了。都不能用时，原因取最有希望的那把：有卡在并发上的就说不准什么时候恢复；
        // 否则取最早恢复的；全是白名单不允许才算白名单。
        List<(BlockReason Reason, DateTimeOffset? RecoversAt)> keyBlocks = [];
        foreach (ChannelKeySnapshot key in candidate.Channel.Keys)
        {
            if (cooldowns.CheckKey(key.Fingerprint, candidate.UpstreamModel) is { } keyCooling)
            {
                keyBlocks.Add((BlockReason.Cooldown, keyCooling.Until));
            }
            else if (restrictions?.CheckKey(key, candidate.UpstreamModel) is { } restricted)
            {
                keyBlocks.Add(restricted);
            }
            else
            {
                return null;
            }
        }

        List<(BlockReason Reason, DateTimeOffset? RecoversAt)> hopeful =
            [.. keyBlocks.Where(b => b.Reason != BlockReason.KeyModelWhitelist)];
        if (hopeful.Count == 0)
        {
            return new CandidateBlock(candidate, BlockReason.KeyModelWhitelist, null);
        }

        if (hopeful.Any(b => b.Reason == BlockReason.Concurrency))
        {
            return new CandidateBlock(candidate, BlockReason.Concurrency, null);
        }

        (BlockReason Reason, DateTimeOffset? RecoversAt) soonest = hopeful.MinBy(b => b.RecoversAt ?? DateTimeOffset.MaxValue);
        return new CandidateBlock(candidate, soonest.Reason, soonest.RecoversAt);
    }

    private static DateTimeOffset? Earliest(List<CandidateBlock> blocks)
    {
        DateTimeOffset? earliest = null;
        foreach (CandidateBlock block in blocks)
        {
            if (block.Reason == BlockReason.KeyModelWhitelist)
            {
                continue;
            }

            if (block.RecoversAt is not { } at)
            {
                // 有一个说不准，整体就说不准（Q91）。
                return null;
            }

            earliest = Earlier(earliest, at);
        }

        return earliest;
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? current, DateTimeOffset? candidate) =>
        (current, candidate) switch
        {
            (null, _) => candidate,
            (_, null) => current,
            _ => candidate < current ? candidate : current,
        };
}

/// <summary>一个候选此刻的状态：被挡时 <see cref="Block"/> 不为 null。</summary>
public sealed record CandidateState(
    RouteCandidate Candidate,
    CandidateBlock? Block,
    HealthSample Health,
    int EffectiveWeight);
