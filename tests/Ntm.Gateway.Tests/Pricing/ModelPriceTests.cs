using Ntm.Gateway.Pricing;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>按总输入挑长上下文单价（Q66）。</summary>
public sealed class ModelPriceTests
{
    private static readonly PriceSet Base = new(1.25m, 0.125m, null, null, 10m);
    private static readonly PriceSet Over200K = new(2.5m, 0.25m, null, null, 15m);
    private static readonly PriceSet Over1M = new(5m, 0.5m, null, null, 20m);

    private static readonly ModelPrice Price = new(
        Base,
        [new LongContextPrice(200_000, Over200K), new LongContextPrice(1_000_000, Over1M)]);

    [Theory]
    [InlineData(0L, null)]
    [InlineData(200_000L, null)]
    [InlineData(200_001L, 200_000L)]
    [InlineData(1_000_000L, 200_000L)]
    [InlineData(1_000_001L, 1_000_000L)]
    public void TheHighestExceededThresholdWins(long totalInput, long? expectedThreshold)
    {
        (PriceSet prices, long? threshold) = Price.For(totalInput);

        Assert.Equal(expectedThreshold, threshold);
        PriceSet expected = expectedThreshold switch
        {
            null => Base,
            200_000L => Over200K,
            _ => Over1M,
        };
        Assert.Equal(expected, prices);
    }

    [Fact]
    public void WithoutLongContextPricesTheBasePriceAlwaysApplies()
    {
        var price = new ModelPrice(Base, CatalogPrice.NoLongContext);

        Assert.Equal((Base, (long?)null), price.For(5_000_000));
    }
}
