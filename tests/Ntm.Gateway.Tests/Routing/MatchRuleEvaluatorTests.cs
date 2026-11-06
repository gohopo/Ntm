using Ntm.Gateway.Configuration;
using Ntm.Gateway.Routing;

namespace Ntm.Gateway.Tests.Routing;

public class MatchRuleEvaluatorTests
{
    private static MatchableChannel Channel(
        int id, string name, int priority, int weight, params string[] models) =>
        new()
        {
            ChannelId = id,
            ChannelName = name,
            Priority = priority,
            Weight = weight,
            UpstreamModels = models,
        };

    private static MatchRule Rule(
        int id,
        int ordinal,
        MatchKind kind,
        string pattern,
        int? channelId = null,
        int? priority = null,
        int? weight = null,
        bool enabled = true)
    {
        var rule = new MatchRule
        {
            Id = id,
            Ordinal = ordinal,
            Kind = kind,
            Pattern = pattern,
            ChannelId = channelId,
            Priority = priority,
            Weight = weight,
            Enabled = enabled,
        };
        return rule;
    }

    [Fact]
    public void AnExactRuleOnAllChannelsFindsEveryChannelListingThatModel()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Exact, "gpt-4o")],
            [
                Channel(1, "a", 10, 1, "gpt-4o", "gpt-4o-mini"),
                Channel(2, "b", 10, 1, "gpt-4o"),
                Channel(3, "c", 10, 1, "claude-sonnet-4-5"),
            ]);

        Assert.Empty(result.Problems);
        Assert.Equal([(1, "gpt-4o"), (2, "gpt-4o")], result.Candidates.Select(c => (c.ChannelId, c.UpstreamModel)));
    }

    [Fact]
    public void AChannelThatDoesNotListTheModelNeverMatches()
    {
        // 清单是唯一的事实来源：限定渠道的精确规则也不能凭空命中。
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Exact, "gpt-5", channelId: 1)],
            [Channel(1, "a", 0, 1, "gpt-4o")]);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void OrderingIsDeterministicWithinOneRule()
    {
        // AxonHub 在这里遍历 Go map，顺序每个进程都不同。
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Regex, "gpt-4o.*")],
            [
                Channel(7, "low", 0, 1, "gpt-4o-mini", "gpt-4o"),
                Channel(3, "high", 10, 1, "gpt-4o-audio", "gpt-4o"),
            ]);

        Assert.Equal(
            [(3, "gpt-4o"), (3, "gpt-4o-audio"), (7, "gpt-4o"), (7, "gpt-4o-mini")],
            result.Candidates.Select(c => (c.ChannelId, c.UpstreamModel)));
    }

    [Fact]
    public void TheFirstRuleToClaimAPairKeepsIt()
    {
        // 第二条规则给了不同的权重，但抢不走已经被认领的落点。
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [
                Rule(1, 0, MatchKind.Exact, "gpt-4o", weight: 5),
                Rule(2, 1, MatchKind.Regex, "gpt-.*", weight: 99),
            ],
            [Channel(1, "a", 0, 1, "gpt-4o", "gpt-4o-mini")]);

        MatchedCandidate claimed = Assert.Single(result.Candidates, c => c.UpstreamModel == "gpt-4o");
        Assert.Equal(5, claimed.Weight);
        Assert.Equal(0, claimed.RuleOrdinal);

        MatchedCandidate later = Assert.Single(result.Candidates, c => c.UpstreamModel == "gpt-4o-mini");
        Assert.Equal(99, later.Weight);
        Assert.Equal(1, later.RuleOrdinal);
    }

    [Fact]
    public void RulesAreProcessedByOrdinalNotByListOrder()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [
                Rule(9, 1, MatchKind.Regex, "gpt-.*", weight: 99),
                Rule(2, 0, MatchKind.Exact, "gpt-4o", weight: 5),
            ],
            [Channel(1, "a", 0, 1, "gpt-4o")]);

        Assert.Equal(5, Assert.Single(result.Candidates).Weight);
    }

    [Fact]
    public void AChannelScopedRuleIgnoresOtherChannels()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Regex, "gpt-.*", channelId: 2)],
            [Channel(1, "a", 0, 1, "gpt-4o"), Channel(2, "b", 0, 1, "gpt-4o")]);

        Assert.Equal(2, Assert.Single(result.Candidates).ChannelId);
    }

    [Fact]
    public void ExcludedChannelIdsAreSkipped()
    {
        MatchRule rule = Rule(1, 0, MatchKind.Regex, "gpt-.*");
        rule.ExcludedChannelIds.Add(2);

        MatchResult result = MatchRuleEvaluator.Evaluate(
            [rule],
            [Channel(1, "a", 0, 1, "gpt-4o"), Channel(2, "b", 0, 1, "gpt-4o")]);

        Assert.Equal(1, Assert.Single(result.Candidates).ChannelId);
    }

    [Fact]
    public void ExcludedPatternsRemoveMatchedModels()
    {
        // NonBacktracking 没有负向前查找，所以排除是独立字段（ADR-0011）。
        MatchRule rule = Rule(1, 0, MatchKind.Regex, "gpt-4o.*");
        rule.ExcludedPatterns.Add(".*-audio");

        MatchResult result = MatchRuleEvaluator.Evaluate(
            [rule],
            [Channel(1, "a", 0, 1, "gpt-4o", "gpt-4o-mini", "gpt-4o-audio")]);

        Assert.Equal(["gpt-4o", "gpt-4o-mini"], result.Candidates.Select(c => c.UpstreamModel));
    }

    [Fact]
    public void RuleOverridesWinOverChannelDefaults()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Exact, "gpt-4o", priority: 50, weight: 7)],
            [Channel(1, "a", 10, 1, "gpt-4o")]);

        MatchedCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(50, candidate.Priority);
        Assert.Equal(7, candidate.Weight);
    }

    [Fact]
    public void WithoutOverridesTheChannelDefaultsApply()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Exact, "gpt-4o")],
            [Channel(1, "a", 10, 4, "gpt-4o")]);

        MatchedCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(10, candidate.Priority);
        Assert.Equal(4, candidate.Weight);
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Exact, "gpt-4o", enabled: false)],
            [Channel(1, "a", 0, 1, "gpt-4o")]);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void AnInvalidPatternIsReportedAndTheRuleIsSkipped()
    {
        // AxonHub 在这里 fail closed 且静默：规则悄悄匹配空集，用户没有任何提示。
        MatchResult result = MatchRuleEvaluator.Evaluate(
            [Rule(1, 0, MatchKind.Regex, "gpt-4["), Rule(2, 1, MatchKind.Exact, "gpt-4o")],
            [Channel(1, "a", 0, 1, "gpt-4o")]);

        RuleProblem problem = Assert.Single(result.Problems);
        Assert.Equal(1, problem.RuleId);
        Assert.Contains("语法错误", problem.Message, StringComparison.Ordinal);

        // 坏规则不影响其他规则。
        Assert.Equal("gpt-4o", Assert.Single(result.Candidates).UpstreamModel);
    }

    [Fact]
    public void ABrokenExclusionDisablesTheWholeRule()
    {
        // 只有一半排除规则生效，等于悄悄放进了本该排除的模型。
        MatchRule rule = Rule(1, 0, MatchKind.Regex, "gpt-.*");
        rule.ExcludedPatterns.Add(".*-audio");
        rule.ExcludedPatterns.Add("(?!x)");

        MatchResult result = MatchRuleEvaluator.Evaluate([rule], [Channel(1, "a", 0, 1, "gpt-4o", "gpt-4o-audio")]);

        Assert.Empty(result.Candidates);
        Assert.Contains("排除模式", Assert.Single(result.Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRulesMeansNoCandidates()
    {
        MatchResult result = MatchRuleEvaluator.Evaluate([], [Channel(1, "a", 0, 1, "gpt-4o")]);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Problems);
    }
}
