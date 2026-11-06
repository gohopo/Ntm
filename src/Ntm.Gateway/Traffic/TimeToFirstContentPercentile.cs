namespace Ntm.Gateway.Traffic;

/// <summary>
/// 从首字时间的十个桶估分位数（Q45）。不存原始样本，所以只能估：
/// 先找到目标名次落在哪个桶，再在桶的上下界之间按名次线性插值。
/// </summary>
public static class TimeToFirstContentPercentile
{
    /// <summary>各桶的下界，毫秒，和 <see cref="HourlyAggregate.TimeToFirstContentBuckets"/> 一一对应。</summary>
    private static readonly long[] LowerBounds = [0, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 64_000];

    /// <summary>最后一个桶（≥64s）没有上界：落在里面就报下界，界面显示成「≥64 秒」。</summary>
    public const long OpenEndedMilliseconds = 64_000;

    /// <summary>估第 <paramref name="percentile"/>（0–1）分位的首字时间，毫秒；没有样本时为 null。</summary>
    public static long? Estimate(IReadOnlyList<long> buckets, double percentile)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count != LowerBounds.Length)
        {
            throw new ArgumentException($"首字时间应该有 {LowerBounds.Length} 个桶。", nameof(buckets));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);

        long total = 0;
        foreach (long count in buckets)
        {
            total += count;
        }

        if (total == 0)
        {
            return null;
        }

        double rank = percentile * total;
        long before = 0;
        for (int i = 0; i < buckets.Count; i++)
        {
            long count = buckets[i];
            if (count == 0 || before + count < rank)
            {
                before += count;
                continue;
            }

            if (i == buckets.Count - 1)
            {
                return OpenEndedMilliseconds;
            }

            double fraction = Math.Clamp((rank - before) / count, 0, 1);
            long lower = LowerBounds[i];
            long upper = LowerBounds[i + 1];
            return lower + (long)Math.Round(fraction * (upper - lower), MidpointRounding.AwayFromZero);
        }

        return OpenEndedMilliseconds;
    }
}
