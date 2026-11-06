using Ntm.Gateway.Pricing;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>匹配规则（Q68）：段序列连续出现，最长的胜出，并列且单价不同记为有歧义。</summary>
public sealed class PriceMatcherTests
{
    private static CatalogPrice Entry(string modelId, decimal input = 1m) =>
        new("anthropic", modelId, new PriceSet(input, null, null, null, input * 5m), CatalogPrice.NoLongContext);

    [Fact]
    public void DotsAndDashesAreTheSameSeparator()
    {
        Assert.Equal(["claude", "sonnet", "4", "5"], PriceMatcher.Segments("claude-sonnet-4.5"));
        Assert.Equal(["us", "anthropic", "claude", "sonnet", "4", "5", "20250929", "v1", "0"],
            PriceMatcher.Segments("us.anthropic.claude-sonnet-4-5-20250929-v1:0"));
        Assert.Empty(PriceMatcher.Segments(""));
        Assert.Empty(PriceMatcher.Segments("///"));
    }

    [Theory]
    // 点和连字符同义。
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4-5")]
    // 带日期后缀、带 Bedrock 前缀、带网关前缀的都能配上。
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("azure/claude-haiku-4-5", "claude-haiku-4-5")]
    // 最长的胜出：有 claude-opus-4-5 就不会退回 claude-opus。
    [InlineData("claude-opus-4-5", "claude-opus-4-5")]
    public void TheLongestContiguousMatchWins(string upstreamModel, string expected)
    {
        CatalogPrice[] candidates =
        [
            Entry("claude-opus"),
            Entry("claude-opus-4-5"),
            Entry("claude-sonnet-4-5"),
            Entry("claude-haiku-4-5"),
        ];

        PriceMatch match = PriceMatcher.Match(upstreamModel, candidates);

        Assert.Equal(PriceMatchKind.Matched, match.Kind);
        Assert.Equal(expected, match.Entry!.ModelId);
    }

    [Fact]
    public void AShorterCatalogNameIsNeverStretchedToFitALongerUpstreamName()
    {
        // 不做反向匹配：目录里只有 grok-4-5 时，上游的 grok-4 配不上。
        PriceMatch match = PriceMatcher.Match("grok-4", [Entry("grok-4-5"), Entry("grok-4-3")]);

        Assert.Equal(PriceMatchKind.NoMatch, match.Kind);
        Assert.Null(match.Entry);
    }

    [Fact]
    public void SegmentsMustAppearSideBySide()
    {
        // gpt-4-turbo 的段串不连续出现在 gpt-4-1-turbo 里。
        Assert.Equal(PriceMatchKind.NoMatch, PriceMatcher.Match("gpt-4-1-turbo", [Entry("gpt-4-turbo")]).Kind);
    }

    [Fact]
    public void ATieWithDifferentPricesIsAmbiguousAndListsTheCandidates()
    {
        // 段的顺序要对：20250929-claude-sonnet-4-5 的段串不在 claude-sonnet-4-5-20250929 里，所以不算并列。
        PriceMatch ordered = PriceMatcher.Match(
            "claude-sonnet-4-5-20250929",
            [Entry("claude-sonnet-4-5", 3m), Entry("20250929-claude-sonnet-4-5", 9m)]);

        Assert.Equal(PriceMatchKind.Matched, ordered.Kind);
        Assert.Equal("claude-sonnet-4-5", ordered.Entry!.ModelId);

        // 段数一样且单价不同才算并列有歧义。
        PriceMatch tie = PriceMatcher.Match(
            "claude-sonnet-4-5",
            [Entry("claude-sonnet-4-5", 3m), new CatalogPrice(
                "anthropic",
                "claude.sonnet.4.5",
                new PriceSet(9m, null, null, null, 45m),
                CatalogPrice.NoLongContext)]);

        Assert.Equal(PriceMatchKind.Ambiguous, tie.Kind);
        Assert.Null(tie.Entry);
        Assert.Equal(["claude-sonnet-4-5", "claude.sonnet.4.5"], tie.Candidates);
    }

    [Fact]
    public void ATieWithTheSamePriceIsNotAmbiguous()
    {
        PriceMatch match = PriceMatcher.Match(
            "claude-sonnet-4-5",
            [Entry("claude-sonnet-4-5", 3m), Entry("claude.sonnet.4.5", 3m)]);

        Assert.Equal(PriceMatchKind.Matched, match.Kind);
        Assert.Equal(3m, match.Entry!.Prices.Input);
    }

    [Fact]
    public void MatchingIsLimitedToTheProviderTheChannelDeclared()
    {
        var catalog = new PriceCatalog(
            [new CatalogProvider("anthropic", "Anthropic", 1), new CatalogProvider("openai", "OpenAI", 1)],
            [
                new CatalogPrice("anthropic", "claude-sonnet-4-5", new PriceSet(3m, null, null, null, 15m), CatalogPrice.NoLongContext),
                new CatalogPrice("openai", "gpt-4.1", new PriceSet(2m, null, null, null, 8m), CatalogPrice.NoLongContext),
            ],
            etag: null,
            fetchedAt: null);

        Assert.True(PriceMatcher.TryMatch(catalog, "openai", "gpt-4.1-2025-04-14", out CatalogPrice? entry));
        Assert.Equal("gpt-4.1", entry.ModelId);

        // 同一个名字在别的 Provider 下配不上，哪怕目录里有。
        Assert.False(PriceMatcher.TryMatch(catalog, "anthropic", "gpt-4.1-2025-04-14", out _));

        // 「不在列表里」（空 Provider）永远配不上。
        Assert.False(PriceMatcher.TryMatch(catalog, "", "gpt-4.1", out _));
    }
}
