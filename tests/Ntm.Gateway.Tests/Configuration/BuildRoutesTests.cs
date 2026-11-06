using Ntm.Gateway.Configuration;
using Ntm.Gateway.Routing;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Configuration;

/// <summary>
/// 匹配规则展开成候选、再按 Priority 分档的那一步。它在建快照时跑一次，
/// 热路径上就不用再跑正则了。
/// </summary>
public class BuildRoutesTests
{
    private static ChannelSnapshot Channel(
        int id,
        int priority = 0,
        int weight = 1,
        ApiFormat format = ApiFormat.OpenAiChat,
        bool withKey = true,
        params string[] models) => new()
        {
            ChannelId = id,
            ChannelName = "ch" + id,
            BaseUrl = $"https://ch{id}.example",
            ApiFormat = format,
            Priority = priority,
            Weight = weight,
            Keys = withKey
                ? [new ChannelKeySnapshot
                {
                    ChannelKeyId = id,
                    Name = "key",
                    Secret = "s" + id,
                    Fingerprint = "fp" + id,
                    Prefix = "s",
                }]
                : [],
            UpstreamModels = models,
        };

    private static PublishedModel Model(
        string name,
        RequestKind kind = RequestKind.Chat,
        bool enabled = true,
        params MatchRule[] rules)
    {
        var model = new PublishedModel { Id = 1, Name = name, RequestKind = kind, Enabled = enabled };
        foreach (MatchRule rule in rules)
        {
            model.Rules.Add(rule);
        }

        return model;
    }

    private static MatchRule Exact(string pattern, int ordinal = 1, int? channelId = null) => new()
    {
        Id = ordinal,
        Ordinal = ordinal,
        Kind = MatchKind.Exact,
        Pattern = pattern,
        ChannelId = channelId,
    };

    private static MatchRule Regex(string pattern, int ordinal = 1, int? channelId = null) => new()
    {
        Id = ordinal,
        Ordinal = ordinal,
        Kind = MatchKind.Regex,
        Pattern = pattern,
        ChannelId = channelId,
    };

    [Fact]
    public void AnExactRuleMatchesThatOneUpstreamModelOnEveryChannel()
    {
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: Exact("gpt-4o"))],
            [Channel(1, models: ["gpt-4o", "gpt-4o-mini"]), Channel(2, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(2, route.CandidateCount);
        Assert.All(route.PriorityGroups.SelectMany(t => t.Candidates), c => Assert.Equal("gpt-4o", c.UpstreamModel));
    }

    [Fact]
    public void ARegexRuleTakesEveryMatchingUpstreamModel()
    {
        // 正则设计的收益：一条规则吃下同渠道的多个上游模型，将来新增的也自动进来。
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("claude", rules: Regex("claude-.*"))],
            [Channel(1, models: ["claude-opus", "claude-sonnet", "gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(2, route.CandidateCount);
        Assert.Equal(
            ["claude-opus", "claude-sonnet"],
            route.PriorityGroups[0].Candidates.Select(c => c.UpstreamModel));
    }

    [Fact]
    public void ARuleCannotConjureAModelTheChannelDoesNotList()
    {
        // 渠道的上游模型清单是唯一的事实来源。
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("ghost", rules: Exact("not-served"))],
            [Channel(1, models: ["gpt-4o"])]);

        Assert.Equal(0, Assert.Single(routes).CandidateCount);
    }

    [Fact]
    public void CandidatesAreGroupedIntoPriorityTiersHighestFirst()
    {
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: Exact("gpt-4o"))],
            [Channel(1, priority: 5, models: ["gpt-4o"]), Channel(2, priority: 50, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal([50, 5], route.PriorityGroups.Select(t => t.Priority));
        Assert.Equal(2, route.PriorityGroups[0].Candidates[0].ChannelId);
    }

    [Fact]
    public void AChannelWithNoKeysNeverBecomesACandidate()
    {
        // 没密钥的渠道参与匹配只会先到先得地占住落点，却永远打不出去。
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: Exact("gpt-4o"))],
            [Channel(1, withKey: false, models: ["gpt-4o"]), Channel(2, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(1, route.CandidateCount);
        Assert.Equal(2, route.PriorityGroups[0].Candidates[0].ChannelId);
    }

    [Fact]
    public void AFormatThatCannotServeTheRequestKindIsExcluded()
    {
        // Anthropic 没有嵌入端点：这种渠道不该成为嵌入请求的候选。
        // AxonHub 只在列模型时查能力，于是嵌入请求被路由到 Anthropic 渠道后死在转换器里。
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("embed", kind: RequestKind.Embedding, rules: Exact("text-embedding-3"))],
            [
                Channel(1, format: ApiFormat.Anthropic, models: ["text-embedding-3"]),
                Channel(2, format: ApiFormat.OpenAiChat, models: ["text-embedding-3"]),
            ]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(1, route.CandidateCount);
        Assert.Equal(2, route.PriorityGroups[0].Candidates[0].ChannelId);
    }

    [Fact]
    public void ARuleScopedToOneChannelIgnoresTheOthers()
    {
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: Exact("gpt-4o", channelId: 2))],
            [Channel(1, models: ["gpt-4o"]), Channel(2, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(1, route.CandidateCount);
        Assert.Equal(2, route.PriorityGroups[0].Candidates[0].ChannelId);
    }

    [Fact]
    public void TheFirstRuleToClaimAPairKeepsItsOverrides()
    {
        // 先到先得按（渠道, 上游模型）判定，后面的规则抢不走，也就抢不走它的优先级和权重。
        var first = Exact("gpt-4o", ordinal: 1);
        first.Weight = 7;
        var second = Exact("gpt-4o", ordinal: 2);
        second.Weight = 99;

        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: [first, second])],
            [Channel(1, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.Equal(1, route.CandidateCount);
        Assert.Equal(7, route.PriorityGroups[0].Candidates[0].Weight);
    }

    [Fact]
    public void AnInvalidPatternIsReportedAndThatRuleIsSkipped()
    {
        // 面板要能告诉用户哪条规则坏了。AxonHub 在匹配时静默 fail closed，用户只看到「没有候选」。
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", rules: [Regex("gpt-4[", ordinal: 1), Exact("gpt-4o", ordinal: 2)])],
            [Channel(1, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        RuleProblem problem = Assert.Single(route.Problems);
        Assert.Equal(1, problem.RuleOrdinal);
        Assert.Contains("正则", problem.Message, StringComparison.Ordinal);

        // 坏规则不拖累别的规则。
        Assert.Equal(1, route.CandidateCount);
    }

    [Fact]
    public void ADisabledModelStillGetsARouteSoThePanelCanShowIt()
    {
        List<PublishedModelRoute> routes = ConfigSnapshotProvider.BuildRoutes(
            [Model("fast", enabled: false, rules: Exact("gpt-4o"))],
            [Channel(1, models: ["gpt-4o"])]);

        PublishedModelRoute route = Assert.Single(routes);
        Assert.False(route.Enabled);
        Assert.Equal(1, route.CandidateCount);
    }

    [Fact]
    public void OnlyEnabledModelsAreReachableByName()
    {
        var snapshot = new ConfigSnapshot(
            new Dictionary<string, ApiKeySnapshot>(),
            [],
            ConfigSnapshotProvider.BuildRoutes(
                [
                    Model("live", rules: Exact("gpt-4o")),
                    new PublishedModel { Id = 2, Name = "draft", Enabled = false },
                ],
                [Channel(1, models: ["gpt-4o"])]));

        Assert.NotNull(snapshot.FindRoute("live"));
        Assert.Null(snapshot.FindRoute("draft"));
        Assert.Equal(2, snapshot.Routes.Count);
    }

    [Fact]
    public void ModelNamesAreCaseSensitive()
    {
        var snapshot = new ConfigSnapshot(
            new Dictionary<string, ApiKeySnapshot>(),
            [],
            ConfigSnapshotProvider.BuildRoutes(
                [Model("GPT-4o", rules: Exact("gpt-4o"))],
                [Channel(1, models: ["gpt-4o"])]));

        Assert.NotNull(snapshot.FindRoute("GPT-4o"));
        Assert.Null(snapshot.FindRoute("gpt-4o"));
    }
}
