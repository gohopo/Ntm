using Ntm.Gateway.Pricing;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>Usage × 单价 × 倍率 = 费用（Q49/Q65/Q66）。</summary>
public sealed class CostCalculatorTests
{
    private static readonly PriceSet Sonnet = new(3m, 0.3m, 3.75m, 6m, 15m);

    private static AttemptRecord Attempt(
        long input = 0,
        long cacheRead = 0,
        long cacheWrite5m = 0,
        long cacheWrite1h = 0,
        long output = 0,
        int multiplierBasisPoints = 10_000) =>
        new()
        {
            ChannelName = "c",
            ChannelKeyFingerprint = "fp",
            ChannelKeyPrefix = "sk-x",
            UpstreamModel = "claude-sonnet-4-5",
            RequestUri = "https://example/v1/messages",
            InputTokens = input,
            CacheReadTokens = cacheRead,
            CacheWrite5mTokens = cacheWrite5m,
            CacheWrite1hTokens = cacheWrite1h,
            OutputTokens = output,
            MultiplierBasisPoints = multiplierBasisPoints,
        };

    [Fact]
    public void EveryPartIsPricedAndTheSnapshotIsKept()
    {
        AttemptRecord attempt = Attempt(input: 1000, cacheRead: 2000, cacheWrite5m: 500, output: 300);

        CostCalculator.Apply(attempt, new ModelPrice(Sonnet, CatalogPrice.NoLongContext));

        // 单价是「美元 / 每百万 Token」，纳美元 = Token × 单价 × 1000：
        // 1000×3×1000 + 2000×0.3×1000 + 500×3.75×1000 + 300×15×1000 = 9_975_000 纳美元（$0.009975）
        Assert.Equal(9_975_000, attempt.CostNanoUsd);
        Assert.Equal(3m, attempt.InputPrice);
        Assert.Equal(0.3m, attempt.CacheReadPrice);
        Assert.Equal(3.75m, attempt.CacheWrite5mPrice);
        Assert.Equal(6m, attempt.CacheWrite1hPrice);
        Assert.Equal(15m, attempt.OutputPrice);
        Assert.Null(attempt.LongContextOver);
    }

    [Fact]
    public void TheMultiplierScalesTheResult()
    {
        AttemptRecord attempt = Attempt(input: 1_000_000, multiplierBasisPoints: 15_000);

        CostCalculator.Apply(attempt, new ModelPrice(Sonnet, CatalogPrice.NoLongContext));

        // 1M × $3 × 1.5 = $4.5 = 4_500_000_000 纳美元
        Assert.Equal(4_500_000_000, attempt.CostNanoUsd);
    }

    [Fact]
    public void AMissingPriceOnAUsedPartLeavesTheWholeAttemptUnpriced()
    {
        var partial = new PriceSet(3m, null, null, null, 15m);
        AttemptRecord attempt = Attempt(input: 1000, cacheRead: 2000, output: 300);

        CostCalculator.Apply(attempt, new ModelPrice(partial, CatalogPrice.NoLongContext));

        Assert.Null(attempt.CostNanoUsd);
        // 快照还是记下来，页面上能看出是哪一项缺单价。
        Assert.Equal(3m, attempt.InputPrice);
        Assert.Null(attempt.CacheReadPrice);
    }

    [Fact]
    public void AMissingPriceOnAnUnusedPartDoesNotMatter()
    {
        var partial = new PriceSet(3m, null, null, null, 15m);
        AttemptRecord attempt = Attempt(input: 1000, output: 300);

        CostCalculator.Apply(attempt, new ModelPrice(partial, CatalogPrice.NoLongContext));

        // 1000×3×1000 + 300×15×1000 = 7_500_000 纳美元
        Assert.Equal(7_500_000, attempt.CostNanoUsd);
    }

    [Fact]
    public void NoPriceRowAtAllMeansNoCostAndNoSnapshot()
    {
        AttemptRecord attempt = Attempt(input: 1000, output: 300);

        CostCalculator.Apply(attempt, null);

        Assert.Null(attempt.CostNanoUsd);
        Assert.Null(attempt.InputPrice);
        Assert.Null(attempt.OutputPrice);
    }

    [Fact]
    public void LongContextUsesTheGroupPriceForTheWholeRequestAndRecordsTheThreshold()
    {
        var over200K = new PriceSet(6m, 0.6m, 7.5m, 12m, 22.5m);
        var price = new ModelPrice(Sonnet, [new LongContextPrice(200_000, over200K)]);
        AttemptRecord attempt = Attempt(input: 150_000, cacheRead: 60_000, output: 1000);

        CostCalculator.Apply(attempt, price);

        // 总输入 210_000 > 200_000：输入和输出都按长上下文单价。
        // 150_000×6×1000 + 60_000×0.6×1000 + 1000×22.5×1000 = 958_500_000 纳美元（$0.9585）
        Assert.Equal(958_500_000, attempt.CostNanoUsd);
        Assert.Equal(200_000, attempt.LongContextOver);
        Assert.Equal(6m, attempt.InputPrice);
        Assert.Equal(22.5m, attempt.OutputPrice);
    }

    [Fact]
    public void ZeroUsageCostsNothing()
    {
        AttemptRecord attempt = Attempt();

        CostCalculator.Apply(attempt, new ModelPrice(Sonnet, CatalogPrice.NoLongContext));

        Assert.Equal(0, attempt.CostNanoUsd);
    }

    [Fact]
    public void WhenThinkingWasEmittedTheWholeOutputUsesTheThinkingPrice()
    {
        var qwen = new ModelPrice(new PriceSet(0.4m, 0.08m, null, null, 1.2m, ThinkingOutput: 4m), CatalogPrice.NoLongContext);

        AttemptRecord thinking = Attempt(input: 1000, output: 1000);
        thinking.SawThinking = true;
        CostCalculator.Apply(thinking, qwen);

        // 1000×0.4×1000 + 1000×4×1000 = 4_400_000：思考和回答都按思考价，不拆分（ADR-0013）。
        Assert.Equal(4_400_000, thinking.CostNanoUsd);
        Assert.Equal(4m, thinking.OutputPrice);

        AttemptRecord plain = Attempt(input: 1000, output: 1000);
        CostCalculator.Apply(plain, qwen);
        Assert.Equal(1_600_000, plain.CostNanoUsd);
        Assert.Equal(1.2m, plain.OutputPrice);
    }

    [Fact]
    public void WithoutAThinkingPriceThinkingFallsBackToTheOutputPriceInsteadOfBeingUnpriced()
    {
        AttemptRecord attempt = Attempt(output: 1000);
        attempt.SawThinking = true;

        CostCalculator.Apply(attempt, new ModelPrice(Sonnet, CatalogPrice.NoLongContext));

        Assert.Equal(15_000_000, attempt.CostNanoUsd);
        Assert.Equal(15m, attempt.OutputPrice);
    }

    [Fact]
    public void LongContextTiersCarryTheirOwnThinkingPrice()
    {
        var price = new ModelPrice(
            new PriceSet(0.4m, null, null, null, 1.2m, ThinkingOutput: 4m),
            [new LongContextPrice(256_000, new PriceSet(1.2m, null, null, null, 3.6m, ThinkingOutput: 12m))]);
        AttemptRecord attempt = Attempt(input: 300_000, output: 1000);
        attempt.SawThinking = true;

        CostCalculator.Apply(attempt, price);

        Assert.Equal(12m, attempt.OutputPrice);
        Assert.Equal(256_000, attempt.LongContextOver);
    }
}
