using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Traffic;

public sealed class TimeToFirstContentPercentileTests
{
    private static long[] Buckets(params (int Index, long Count)[] counts)
    {
        var buckets = new long[10];
        foreach ((int index, long count) in counts)
        {
            buckets[index] = count;
        }

        return buckets;
    }

    [Fact]
    public void NoSamplesMeansNoValue() =>
        Assert.Null(TimeToFirstContentPercentile.Estimate(new long[10], 0.5));

    [Fact]
    public void TheValueIsInterpolatedInsideTheBucket()
    {
        long[] buckets = Buckets((3, 10)); // 全在 1s–2s
        Assert.Equal(1_500, TimeToFirstContentPercentile.Estimate(buckets, 0.5));
        Assert.Equal(1_950, TimeToFirstContentPercentile.Estimate(buckets, 0.95));
    }

    [Fact]
    public void ThePercentileWalksAcrossBuckets()
    {
        long[] buckets = Buckets((0, 50), (4, 50)); // 一半 <0.25s，一半 2s–4s
        Assert.Equal(250, TimeToFirstContentPercentile.Estimate(buckets, 0.5));
        Assert.Equal(3_800, TimeToFirstContentPercentile.Estimate(buckets, 0.95));
    }

    [Fact]
    public void TheOpenEndedBucketReportsItsLowerBound()
    {
        long[] buckets = Buckets((0, 1), (9, 99));
        Assert.Equal(TimeToFirstContentPercentile.OpenEndedMilliseconds, TimeToFirstContentPercentile.Estimate(buckets, 0.95));
    }

    [Fact]
    public void TheBucketCountMustMatch() =>
        Assert.Throws<ArgumentException>(() => TimeToFirstContentPercentile.Estimate(new long[3], 0.5));
}
