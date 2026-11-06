using Ntm.Gateway.Routing;

namespace Ntm.Gateway.Tests.Routing;

public class SmoothWeightedRoundRobinTests
{
    private static string Sequence(SmoothWeightedRoundRobin rr, int rounds, int[] keys, int[] weights)
    {
        char[] picks = new char[rounds];
        for (int i = 0; i < rounds; i++)
        {
            picks[i] = (char)('a' + rr.Pick(keys, weights));
        }

        return new string(picks);
    }

    [Fact]
    public void TheClassicFiveOneOneSequence()
    {
        // Nginx 的教科书例子：从零状态开始是 a a b a c a a。
        // 朴素加权轮询会给出 aaaaabc，把五个请求连着压到一个候选上。
        var rr = new SmoothWeightedRoundRobin();

        Assert.Equal("aabacaa", Sequence(rr, 7, [1, 2, 3], [5, 1, 1]));
    }

    [Fact]
    public void TheSequenceRepeatsEveryWeightSum()
    {
        var rr = new SmoothWeightedRoundRobin();

        string first = Sequence(rr, 7, [1, 2, 3], [5, 1, 1]);
        string second = Sequence(rr, 7, [1, 2, 3], [5, 1, 1]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EqualWeightsRotate()
    {
        var rr = new SmoothWeightedRoundRobin();

        Assert.Equal("abcabc", Sequence(rr, 6, [1, 2, 3], [1, 1, 1]));
    }

    [Fact]
    public void ProportionsHoldOverManyRounds()
    {
        var rr = new SmoothWeightedRoundRobin();
        int[] keys = [1, 2];
        int[] weights = [300, 100];
        var counts = new int[2];

        for (int i = 0; i < 400; i++)
        {
            counts[rr.Pick(keys, weights)]++;
        }

        Assert.Equal(300, counts[0]);
        Assert.Equal(100, counts[1]);
    }

    [Fact]
    public void ASingleCandidateIsAlwaysPicked()
    {
        var rr = new SmoothWeightedRoundRobin();

        Assert.Equal(0, rr.Pick([7], [3]));
        Assert.Equal(0, rr.Pick([7], [3]));
    }

    [Fact]
    public void NoCandidatesMeansNoPick() => Assert.Equal(-1, new SmoothWeightedRoundRobin().Pick([], []));

    [Fact]
    public void ACandidateThatDropsOutDoesNotStallTheRest()
    {
        // 候选在冷却期间不出现在可用集里，剩下的照常按比例分流。
        // 留下的候选带着之前的累计值，所以刚切换的几轮会偏向一边（nginx 跳过下线节点时也是这样）；
        // 不在切换时清零，是因为冷却来回抖动时清零会把平滑性也一起清掉。偏差有界，很快拉平。
        var rr = new SmoothWeightedRoundRobin();
        Sequence(rr, 3, [1, 2, 3], [5, 1, 1]);

        var counts = new int[2];
        for (int i = 0; i < 40; i++)
        {
            counts[rr.Pick([2, 3], [1, 1])]++;
        }

        Assert.Equal(40, counts[0] + counts[1]);
        Assert.InRange(counts[0], 17, 23);
        Assert.Contains('a', Sequence(rr, 6, [2, 3], [1, 1]));
        Assert.Contains('b', Sequence(rr, 6, [2, 3], [1, 1]));
    }

    [Fact]
    public void AllZeroWeightsStillReturnACandidate()
    {
        // 健康系数有下限，理论上到不了全零，但真到了也不能让请求无处可去。
        var rr = new SmoothWeightedRoundRobin();

        Assert.Equal(0, rr.Pick([1, 2], [0, 0]));
    }

    [Fact]
    public void MismatchedLengthsAreRejected() =>
        Assert.Throws<ArgumentException>(() => new SmoothWeightedRoundRobin().Pick([1, 2], [1]));

    [Fact]
    public async Task ConcurrentPicksStayWithinTheWeightRatio()
    {
        // 状态有锁：并发下总数仍然正确。
        var rr = new SmoothWeightedRoundRobin();
        int[] keys = [1, 2];
        int[] weights = [3, 1];
        int[] counts = [0, 0];

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                Interlocked.Increment(ref counts[rr.Pick(keys, weights)]);
            }
        })));

        Assert.Equal(800, counts[0] + counts[1]);
        Assert.InRange(counts[0], 560, 640);
    }
}
