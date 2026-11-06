using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Routing;

public class CooldownRegistryTests
{
    private const string Fingerprint = "fp-a";
    private const string Other = "fp-b";
    private const string Model = "gpt-4o";

    // 固定随机数，抖动可预测：0.5 正好是不加不减。
    private static (CooldownRegistry Registry, FakeTimeProvider Time) Create(double random = 0.5)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new CooldownRegistry(time, () => random), time);
    }

    private static DateTimeOffset? Fail(
        CooldownRegistry registry,
        FailureClass failure,
        int channelId = 1,
        string model = Model,
        string fingerprint = Fingerprint,
        DateTimeOffset? resetAt = null)
    {
        CooldownPermit permit = registry.TryAcquire(channelId, model, fingerprint, out _)!;
        Assert.NotNull(permit);
        return registry.Complete(permit, channelId, model, fingerprint, failure, resetAt);
    }

    [Fact]
    public void AFreshRegistryBlocksNothing()
    {
        (CooldownRegistry registry, _) = Create();

        Assert.Null(registry.CheckCandidate(1, Model));
        Assert.Null(registry.CheckKey(Fingerprint, Model));
        Assert.NotNull(registry.TryAcquire(1, Model, Fingerprint, out CooldownBlock? block));
        Assert.Null(block);
    }

    [Fact]
    public void AuthFailureCoolsTheKeyAcrossEveryModelAndChannel()
    {
        // 认证失败是这把密钥的问题，与模型无关：换模型、换渠道都救不了（Q28）。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        DateTimeOffset? until = Fail(registry, FailureClass.AuthFailure);

        Assert.Equal(time.GetUtcNow().AddMinutes(5), until);
        Assert.NotNull(registry.CheckKey(Fingerprint, Model));
        Assert.NotNull(registry.CheckKey(Fingerprint, "another-model"));

        // 候选本身没问题，另一把密钥照常。
        Assert.Null(registry.CheckCandidate(1, Model));
        Assert.Null(registry.CheckKey(Other, Model));
    }

    [Fact]
    public void RateLimitOnlyCoolsThatKeyAndThatModel()
    {
        // 上游限的是账号 × 模型：同一把密钥的其他模型还有额度，同渠道的其他密钥更是（Q28）。
        (CooldownRegistry registry, _) = Create();

        Fail(registry, FailureClass.RateLimited);

        Assert.NotNull(registry.CheckKey(Fingerprint, Model));
        Assert.Null(registry.CheckKey(Fingerprint, "another-model"));
        Assert.Null(registry.CheckKey(Other, Model));
        Assert.Null(registry.CheckCandidate(1, Model));
    }

    [Fact]
    public void OverloadCoolsTheChannelAndModelForEveryKey()
    {
        (CooldownRegistry registry, _) = Create();

        Fail(registry, FailureClass.UpstreamOverloaded);

        CooldownBlock? block = registry.CheckCandidate(1, Model);
        Assert.NotNull(block);
        Assert.Equal(CooldownScope.ChannelModel, block.Scope);
        Assert.False(block.IsKeyScoped);

        // 同渠道的其他模型不受影响。
        Assert.Null(registry.CheckCandidate(1, "another-model"));
    }

    [Fact]
    public void ConnectionFailureCoolsTheWholeChannel()
    {
        (CooldownRegistry registry, _) = Create();

        Fail(registry, FailureClass.ConnectionFailure);

        CooldownBlock? block = registry.CheckCandidate(1, "any-model");
        Assert.NotNull(block);
        Assert.Equal(CooldownScope.Channel, block.Scope);
    }

    [Fact]
    public void ClientRequestErrorsNeverCool()
    {
        // 请求本身的问题（400/413/422）：冷却渠道只会把一个客户端的错误变成所有人的故障。
        (CooldownRegistry registry, _) = Create();

        Assert.Null(Fail(registry, FailureClass.ClientRequestError));
        Assert.Null(Fail(registry, FailureClass.ClientDisconnected));

        Assert.Null(registry.CheckCandidate(1, Model));
        Assert.Null(registry.CheckKey(Fingerprint, Model));
    }

    [Fact]
    public void TheLadderDoublesForTheSameClass()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        DateTimeOffset? first = Fail(registry, FailureClass.RateLimited);
        Assert.Equal(time.GetUtcNow().AddMinutes(1), first);

        time.Advance(TimeSpan.FromMinutes(2));
        DateTimeOffset? second = Fail(registry, FailureClass.RateLimited);
        Assert.Equal(time.GetUtcNow().AddMinutes(2), second);

        time.Advance(TimeSpan.FromMinutes(3));
        DateTimeOffset? third = Fail(registry, FailureClass.RateLimited);
        Assert.Equal(time.GetUtcNow().AddMinutes(4), third);
    }

    [Fact]
    public void ADifferentClassRestartsFromItsOwnInitialDelay()
    {
        // ccLoad 的毛病：加倍时不看新的错误类别，于是紧跟在 60 秒限流后的 401 只冷却 120 秒，
        // 而认证失败本该是 5 分钟起。这里按（作用域, 类别）分开记阶梯。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        Fail(registry, FailureClass.RateLimited);
        time.Advance(TimeSpan.FromMinutes(2));
        Fail(registry, FailureClass.RateLimited);
        time.Advance(TimeSpan.FromMinutes(3));

        DateTimeOffset? auth = Fail(registry, FailureClass.AuthFailure);

        Assert.Equal(time.GetUtcNow().AddMinutes(5), auth);
    }

    [Fact]
    public void TheLadderResetsAfterALongQuietSpell()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        Fail(registry, FailureClass.RateLimited);
        time.Advance(TimeSpan.FromMinutes(2));
        Fail(registry, FailureClass.RateLimited);

        // 超过 LadderResetAfter（1 小时）没再失败：从初始值重新起算。
        time.Advance(TimeSpan.FromHours(2));
        DateTimeOffset? again = Fail(registry, FailureClass.RateLimited);

        Assert.Equal(time.GetUtcNow().AddMinutes(1), again);
    }

    [Fact]
    public void TheBackoffIsCappedAtThirtyMinutes()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        // 1、2、4、8、16 分钟，第六级 32 分钟被封到 30。每次都等过上一次的冷却再失败，
        // 间隔又不到 1 小时，所以阶梯一路往上累。
        for (int i = 0; i < 6; i++)
        {
            Fail(registry, FailureClass.RateLimited);
            time.Advance(TimeSpan.FromMinutes(35));
        }

        DateTimeOffset? until = Fail(registry, FailureClass.RateLimited);

        Assert.Equal(time.GetUtcNow().AddMinutes(30), until);
    }

    [Fact]
    public void JitterMovesTheDelayWithinTwentyPercent()
    {
        (CooldownRegistry low, FakeTimeProvider lowTime) = Create(random: 0);
        (CooldownRegistry high, FakeTimeProvider highTime) = Create(random: 1);

        DateTimeOffset? lowest = Fail(low, FailureClass.RateLimited);
        DateTimeOffset? highest = Fail(high, FailureClass.RateLimited);

        Assert.Equal(lowTime.GetUtcNow().AddSeconds(48), lowest);
        Assert.Equal(highTime.GetUtcNow().AddSeconds(72), highest);
    }

    [Fact]
    public void AnUpstreamResetTimeWinsOverTheLadder()
    {
        // 上游明说了什么时候恢复，就按它来，不叠退避也不叠抖动（Q29）。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        DateTimeOffset reset = time.GetUtcNow().AddSeconds(7);

        DateTimeOffset? until = Fail(registry, FailureClass.RateLimited, resetAt: reset);

        Assert.Equal(reset, until);
    }

    [Fact]
    public void APastResetTimeFallsBackToTheLadder()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        DateTimeOffset? until = Fail(
            registry, FailureClass.RateLimited, resetAt: time.GetUtcNow().AddMinutes(-5));

        Assert.Equal(time.GetUtcNow().AddMinutes(1), until);
    }

    [Fact]
    public void SuccessClearsEveryEnclosingScope()
    {
        // 在（渠道, 密钥, 模型）上成功一次，说明包着它的每一层此刻都是通的。
        (CooldownRegistry registry, _) = Create();
        Fail(registry, FailureClass.AuthFailure);
        Fail(registry, FailureClass.UpstreamOverloaded, fingerprint: Other);

        Assert.NotNull(registry.CheckKey(Fingerprint, Model));
        Assert.NotNull(registry.CheckCandidate(1, Model));

        registry.Clear(1, Model, Fingerprint);

        Assert.Null(registry.CheckKey(Fingerprint, Model));
        Assert.Null(registry.CheckCandidate(1, Model));
    }

    [Fact]
    public void SuccessAlsoResetsTheLadder()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();

        Fail(registry, FailureClass.RateLimited);
        time.Advance(TimeSpan.FromMinutes(2));
        Fail(registry, FailureClass.None);
        time.Advance(TimeSpan.FromMinutes(1));

        DateTimeOffset? until = Fail(registry, FailureClass.RateLimited);

        Assert.Equal(time.GetUtcNow().AddMinutes(1), until);
    }

    [Fact]
    public void OnlyOneRequestProbesAfterTheCooldownExpires()
    {
        // 半开：到期后先只放一个进去试，其他请求仍按冷却处理，
        // 不然一个刚恢复的上游会被堆积的请求一次打回去。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        Fail(registry, FailureClass.UpstreamOverloaded);

        time.Advance(TimeSpan.FromMinutes(2));

        CooldownPermit? prober = registry.TryAcquire(1, Model, Fingerprint, out CooldownBlock? none);
        Assert.NotNull(prober);
        Assert.Null(none);

        Assert.Null(registry.TryAcquire(1, Model, Other, out CooldownBlock? blocked));
        Assert.NotNull(blocked);
        Assert.Equal(CooldownScope.ChannelModel, blocked.Scope);

        // 探测成功：所有人都能用了。
        registry.Complete(prober, 1, Model, Fingerprint, FailureClass.None, null);
        Assert.NotNull(registry.TryAcquire(1, Model, Other, out _));
    }

    [Fact]
    public void AFailedProbeCoolsAgainWithTheNextRung()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        Fail(registry, FailureClass.UpstreamOverloaded);
        time.Advance(TimeSpan.FromMinutes(2));

        DateTimeOffset? until = Fail(registry, FailureClass.UpstreamOverloaded);

        Assert.Equal(time.GetUtcNow().AddMinutes(2), until);
        Assert.NotNull(registry.CheckCandidate(1, Model));
    }

    [Fact]
    public void AProbeThatNeverReportsIsReleasedByItsLease()
    {
        // 拿到探测权的请求崩了或者客户端跑了：租约到期自动放开，不会永远卡着。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        Fail(registry, FailureClass.UpstreamOverloaded);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.NotNull(registry.TryAcquire(1, Model, Fingerprint, out _));
        Assert.Null(registry.TryAcquire(1, Model, Other, out _));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.NotNull(registry.TryAcquire(1, Model, Other, out _));
    }

    [Fact]
    public void ABlockedLaterScopeGivesBackTheProbeItAlreadyClaimed()
    {
        // 渠道+模型到期、但密钥还在冷却：这次尝试发不出去，
        // 它顺手认领的渠道+模型探测权必须放回，否则那一层会被一个没发生的探测卡住。
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        Fail(registry, FailureClass.UpstreamOverloaded);

        // 渠道+模型冷却 1 分钟，密钥冷却 5 分钟。等过前者，后者还在。
        time.Advance(TimeSpan.FromMinutes(2));
        Fail(registry, FailureClass.AuthFailure);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(registry.TryAcquire(1, Model, Fingerprint, out CooldownBlock? block));
        Assert.NotNull(block);
        Assert.Equal(CooldownScope.Key, block.Scope);
        Assert.True(block.IsKeyScoped);

        // 另一把密钥仍然可以去探这个候选。
        Assert.NotNull(registry.TryAcquire(1, Model, Other, out CooldownBlock? none));
        Assert.Null(none);
    }

    [Fact]
    public void ActiveListsWhatIsStillCooling()
    {
        (CooldownRegistry registry, FakeTimeProvider time) = Create();
        Fail(registry, FailureClass.AuthFailure);
        Fail(registry, FailureClass.UpstreamOverloaded, fingerprint: Other);

        IReadOnlyList<(CooldownKey Key, DateTimeOffset Until, FailureClass Failure)> active = registry.Active();

        Assert.Equal(2, active.Count);
        Assert.Contains(active, e => e.Key == CooldownKey.ForKey(Fingerprint) && e.Failure == FailureClass.AuthFailure);
        Assert.Contains(active, e => e.Key == CooldownKey.ForChannelModel(1, Model));

        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(registry.Active());
    }

    [Fact]
    public void TheSameSecretOnTwoChannelsSharesItsCooldown()
    {
        // Q21：同一份密钥值配在两个渠道上时，上游限的是那把密钥，冷却得一起生效。
        (CooldownRegistry registry, _) = Create();

        Fail(registry, FailureClass.AuthFailure, channelId: 1);

        Assert.NotNull(registry.CheckKey(Fingerprint, Model));
        Assert.Null(registry.TryAcquire(2, Model, Fingerprint, out CooldownBlock? block));
        Assert.NotNull(block);
        Assert.Equal(CooldownScope.Key, block.Scope);
    }
}
