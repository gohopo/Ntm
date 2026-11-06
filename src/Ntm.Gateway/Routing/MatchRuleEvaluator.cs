using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Routing;

/// <summary>匹配时看得见的渠道信息。只要这些，匹配就能脱离数据库单独测。</summary>
public sealed class MatchableChannel
{
    public required int ChannelId { get; init; }

    public required string ChannelName { get; init; }

    public int Priority { get; init; }

    public int Weight { get; init; } = 1;

    /// <summary>这个渠道上游提供的模型名。</summary>
    public required IReadOnlyList<string> UpstreamModels { get; init; }
}

/// <summary>一条规则命中的一个落点。</summary>
public sealed class MatchedCandidate
{
    public required int ChannelId { get; init; }

    public required string UpstreamModel { get; init; }

    /// <summary>生效的档位：规则覆盖了就用规则的，否则用渠道的。</summary>
    public required int Priority { get; init; }

    /// <summary>生效的权重，同上。</summary>
    public required int Weight { get; init; }

    /// <summary>认领它的规则在 PublishedModel 内的序号，用于复现排序。</summary>
    public required int RuleOrdinal { get; init; }
}

/// <summary>一条规则的毛病。面板要把它显示出来，而不是让用户对着空的匹配结果猜。</summary>
public sealed class RuleProblem
{
    public required int RuleId { get; init; }

    public required int RuleOrdinal { get; init; }

    public required string Message { get; init; }
}

public sealed class MatchResult
{
    public required IReadOnlyList<MatchedCandidate> Candidates { get; init; }

    public required IReadOnlyList<RuleProblem> Problems { get; init; }
}

/// <summary>
/// 把一个 PublishedModel 的 MatchRule 列表在给定渠道上展开成候选（Q36、Q37）。
///
/// 三条保证：
/// 一条规则命中多个上游模型时顺序确定（先 Priority 降序，再模型名字典序，再渠道 ID），
/// 不像 AxonHub 那样遍历 Go map，每个进程的顺序都不一样；
/// 同一个 (Channel, UpstreamModel) 先到先得，后面的规则抢不走；
/// 正则非法时这条规则整条跳过并记下原因，不静默匹配空集。
/// </summary>
public static class MatchRuleEvaluator
{
    public static MatchResult Evaluate(
        IReadOnlyList<MatchRule> rules,
        IReadOnlyList<MatchableChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(channels);

        List<MatchedCandidate> candidates = [];
        List<RuleProblem> problems = [];

        // 先到先得的认领表，跨规则共享。
        HashSet<(int ChannelId, string UpstreamModel)> claimed = [];

        foreach (MatchRule rule in rules.OrderBy(r => r.Ordinal).ThenBy(r => r.Id))
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (!ModelPattern.TryCreate(rule.Kind, rule.Pattern, out ModelPattern? pattern, out string? error))
            {
                problems.Add(new RuleProblem
                {
                    RuleId = rule.Id,
                    RuleOrdinal = rule.Ordinal,
                    Message = error!,
                });
                continue;
            }

            List<ModelPattern> exclusions = [];
            bool exclusionBroken = false;
            foreach (string excluded in rule.ExcludedPatterns)
            {
                if (ModelPattern.TryCreate(rule.Kind, excluded, out ModelPattern? compiled, out string? excludeError))
                {
                    exclusions.Add(compiled!);
                }
                else
                {
                    problems.Add(new RuleProblem
                    {
                        RuleId = rule.Id,
                        RuleOrdinal = rule.Ordinal,
                        Message = "排除模式「" + excluded + "」无效：" + excludeError,
                    });
                    exclusionBroken = true;
                }
            }

            // 排除模式坏了就整条跳过：带着半套排除规则跑，等于悄悄放进了本该排除的模型。
            if (exclusionBroken)
            {
                continue;
            }

            List<MatchedCandidate> matched = [];
            foreach (MatchableChannel channel in channels)
            {
                if (rule.ChannelId is { } scoped && scoped != channel.ChannelId)
                {
                    continue;
                }

                if (rule.ExcludedChannelIds.Contains(channel.ChannelId))
                {
                    continue;
                }

                foreach (string upstreamModel in channel.UpstreamModels)
                {
                    if (!pattern!.IsMatch(upstreamModel))
                    {
                        continue;
                    }

                    if (exclusions.Exists(e => e.IsMatch(upstreamModel)))
                    {
                        continue;
                    }

                    matched.Add(new MatchedCandidate
                    {
                        ChannelId = channel.ChannelId,
                        UpstreamModel = upstreamModel,
                        Priority = rule.Priority ?? channel.Priority,
                        Weight = rule.Weight ?? channel.Weight,
                        RuleOrdinal = rule.Ordinal,
                    });
                }
            }

            foreach (MatchedCandidate candidate in matched
                .OrderByDescending(c => c.Priority)
                .ThenBy(c => c.UpstreamModel, StringComparer.Ordinal)
                .ThenBy(c => c.ChannelId))
            {
                if (claimed.Add((candidate.ChannelId, candidate.UpstreamModel)))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return new MatchResult { Candidates = candidates, Problems = problems };
    }
}
