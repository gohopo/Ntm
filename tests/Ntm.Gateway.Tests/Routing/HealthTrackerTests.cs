using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Routing;

public class HealthTrackerTests
{
    private const string Model = "gpt-4o";

    private static (HealthTracker Tracker, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new HealthTracker(time), time);
    }

    private static void Record(HealthTracker tracker, FailureClass failure, int times)
    {
        for (int i = 0; i < times; i++)
        {
            tracker.Record(1, Model, failure);
        }
    }

    [Fact]
    public void NoSamplesMeansFullWeight()
    {
        (HealthTracker tracker, _) = Create();

        Assert.Equal(1, tracker.Factor(1, Model));
        Assert.Equal(300, tracker.EffectiveWeight(1, Model, 3));
    }

    [Fact]
    public void AllSuccessesKeepFullWeight()
    {
        (HealthTracker tracker, _) = Create();

        Record(tracker, FailureClass.None, 50);

        Assert.Equal(1, tracker.Factor(1, Model));
    }

    [Fact]
    public void HalfFailuresWithEnoughSamplesHalveTheWeight()
    {
        (HealthTracker tracker, _) = Create();

        Record(tracker, FailureClass.None, 10);
        Record(tracker, FailureClass.UpstreamServerError, 10);

        // 20 个样本刚好到置信阈值：系数 = 1 − 0.5 × 1 = 0.5。
        Assert.Equal(0.5, tracker.Factor(1, Model), 3);
        Assert.Equal(50, tracker.EffectiveWeight(1, Model, 1));
    }

    [Fact]
    public void FewSamplesPullTheFactorTowardsOne()
    {
        // 一个新上线的渠道失败一次不该被打到最低，否则它没有机会证明自己。
        (HealthTracker tracker, _) = Create();

        Record(tracker, FailureClass.UpstreamServerError, 1);

        // 1 个样本：置信度 1/20，系数 = 1 − 1 × 0.05 = 0.95。
        Assert.Equal(0.95, tracker.Factor(1, Model), 3);
    }

    [Fact]
    public void TheFactorNeverFallsBelowTheFloor()
    {
        // 下限 0.1：完全退出的候选永远等不到恢复的机会。
        (HealthTracker tracker, _) = Create();

        Record(tracker, FailureClass.UpstreamServerError, 100);

        Assert.Equal(0.1, tracker.Factor(1, Model), 3);
        Assert.Equal(10, tracker.EffectiveWeight(1, Model, 1));
    }

    [Theory]
    [InlineData(FailureClass.AuthFailure)]
    [InlineData(FailureClass.QuotaExhausted)]
    [InlineData(FailureClass.RateLimited)]
    [InlineData(FailureClass.ClientRequestError)]
    [InlineData(FailureClass.ClientDisconnected)]
    [InlineData(FailureClass.Interrupted)]
    public void CredentialAndClientProblemsDoNotCountAgainstTheChannel(FailureClass failure)
    {
        // 密钥没额度、被限流、客户端自己发错请求：这些由冷却处理，
        // 计入健康度等于拿运维问题惩罚渠道质量。
        (HealthTracker tracker, _) = Create();

        Assert.False(HealthTracker.CountsAgainstHealth(failure));

        Record(tracker, failure, 50);

        Assert.Equal(1, tracker.Factor(1, Model));
    }

    [Theory]
    [InlineData(FailureClass.None)]
    [InlineData(FailureClass.UpstreamOverloaded)]
    [InlineData(FailureClass.UpstreamServerError)]
    [InlineData(FailureClass.FirstEventTimeout)]
    [InlineData(FailureClass.IdleTimeout)]
    [InlineData(FailureClass.StreamInterrupted)]
    [InlineData(FailureClass.ConnectionFailure)]
    [InlineData(FailureClass.SoftError)]
    [InlineData(FailureClass.ModelNotFound)]
    [InlineData(FailureClass.Unknown)]
    public void UpstreamQualityProblemsDoCount(FailureClass failure) =>
        Assert.True(HealthTracker.CountsAgainstHealth(failure));

    [Fact]
    public void SamplesOlderThanTheWindowAreForgotten()
    {
        (HealthTracker tracker, FakeTimeProvider time) = Create();
        Record(tracker, FailureClass.UpstreamServerError, 100);
        Assert.Equal(0.1, tracker.Factor(1, Model), 3);

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(1, tracker.Factor(1, Model));
    }

    [Fact]
    public void RecoveryLiftsTheFactorAsOldFailuresLeaveTheWindow()
    {
        (HealthTracker tracker, FakeTimeProvider time) = Create();
        Record(tracker, FailureClass.UpstreamServerError, 20);

        time.Advance(TimeSpan.FromMinutes(20));
        Record(tracker, FailureClass.None, 20);

        // 两批都还在 30 分钟窗口里：一半一半。
        Assert.Equal(0.5, tracker.Factor(1, Model), 3);

        // 再过 15 分钟，失败那批出窗了。
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(1, tracker.Factor(1, Model));
    }

    [Fact]
    public void EachChannelAndModelIsTrackedSeparately()
    {
        // 健康度按（渠道, 上游模型）记：一个模型过载不代表同渠道的其他模型也不行。
        (HealthTracker tracker, _) = Create();

        Record(tracker, FailureClass.UpstreamServerError, 40);

        Assert.Equal(0.1, tracker.Factor(1, Model), 3);
        Assert.Equal(1, tracker.Factor(1, "another-model"));
        Assert.Equal(1, tracker.Factor(2, Model));
    }

    [Fact]
    public void TheEffectiveWeightIsNeverZero()
    {
        // 权重 0 的候选在平滑加权轮询里永远排不上，等于被悄悄摘掉。
        (HealthTracker tracker, _) = Create();
        Record(tracker, FailureClass.UpstreamServerError, 100);

        Assert.Equal(10, tracker.EffectiveWeight(1, Model, 1));
        Assert.Equal(10, tracker.EffectiveWeight(1, Model, 0));
        Assert.Equal(10, tracker.EffectiveWeight(1, Model, -5));
    }

    [Fact]
    public async Task ConcurrentRecordingStaysConsistent()
    {
        (HealthTracker tracker, _) = Create();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                tracker.Record(1, Model, worker % 2 == 0 ? FailureClass.None : FailureClass.UpstreamServerError);
            }
        })));

        // 一半成功一半失败，样本远超阈值。
        Assert.Equal(0.5, tracker.Factor(1, Model), 2);
    }
}
