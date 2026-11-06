using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Routing;

/// <summary>
/// 选路时怎么看 Restriction（Q78/Q79/Q82/Q91/Q92）：受限的候选退出选路但不进 Cooldown，
/// 能算出恢复时刻的汇总成 EarliestRecovery，卡在并发上的算不出来就不给。
/// </summary>
public sealed class CandidateSelectorRestrictionTests
{
    private const string Model = "gpt-4o";

    private static ChannelSnapshot Channel(
        int id,
        long? dailyLimit = null,
        IReadOnlyList<TimeWindow>? windows = null,
        params ChannelKeySnapshot[] keys) =>
        new()
        {
            ChannelId = id,
            ChannelName = "ch" + id,
            BaseUrl = $"https://ch{id}.example",
            ApiFormat = ApiFormat.OpenAiChat,
            DailyCostLimitNanoUsd = dailyLimit,
            TimeWindows = windows ?? [],
            Keys = keys.Length > 0 ? keys : [Key(id * 10)],
            UpstreamModels = [Model],
        };

    private static ChannelKeySnapshot Key(
        int id,
        int? rpm = null,
        int? concurrency = null,
        params string[] allowedModels) =>
        new()
        {
            ChannelKeyId = id,
            Name = "key" + id,
            Secret = "secret-" + id,
            Fingerprint = "fp-" + id,
            Prefix = "secr",
            RequestsPerMinute = rpm,
            MaxConcurrency = concurrency,
            AllowedModels = [.. allowedModels],
        };

    private static PublishedModelRoute Route(params ChannelSnapshot[] channels) =>
        new()
        {
            PublishedModelId = 1,
            Name = "published",
            RequestKind = RequestKind.Chat,
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            PriorityGroups =
            [
                new PriorityGroup
                {
                    Priority = 0,
                    Candidates = [.. channels.Select(c => new RouteCandidate
                    {
                        Channel = c,
                        UpstreamModel = Model,
                        Priority = 0,
                        Weight = 1,
                    })],
                },
            ],
        };

    private static (CandidateSelector Selector, RestrictionChecker Checker, CostLedger Ledger, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var zone = new GatewayTimeZone(time);
        var ledger = new CostLedger(zone);
        var checker = new RestrictionChecker(ledger, new RequestRateCounter(time), new ConcurrencyGauges(), zone, time);
        var selector = new CandidateSelector(new CooldownRegistry(time, () => 0.5), new HealthTracker(time), checker);
        return (selector, checker, ledger, time);
    }

    private static void RecordCost(CostLedger ledger, int channelId, long cost, DateTimeOffset at)
    {
        var record = new RequestRecord { PublishedModel = Model, StartedAt = at };
        record.Attempts.Add(new AttemptRecord
        {
            StartedAt = at,
            ChannelId = channelId,
            ChannelName = "ch" + channelId,
            ChannelKeyFingerprint = "fp",
            ChannelKeyPrefix = "secr",
            UpstreamModel = Model,
            RequestUri = "https://ch.example",
            CostNanoUsd = cost,
        });
        ledger.Record(record);
    }

    [Fact]
    public void AChannelOverItsDailyLimitLeavesRoutingAndRecoversAtMidnight()
    {
        (CandidateSelector selector, _, CostLedger ledger, FakeTimeProvider time) = Create();
        RecordCost(ledger, channelId: 1, cost: 100, time.GetUtcNow());

        CandidatePlan plan = selector.Plan(Route(Channel(1, dailyLimit: 100)));

        Assert.Empty(plan.Candidates);
        Assert.False(plan.AllCooling);
        Assert.True(plan.HasRestriction);
        Assert.Equal(BlockReason.DailyCostLimit, Assert.Single(plan.Blocks).Reason);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), plan.EarliestRecovery);
    }

    [Fact]
    public void AChannelUnderItsLimitStaysInRouting()
    {
        (CandidateSelector selector, _, CostLedger ledger, FakeTimeProvider time) = Create();
        RecordCost(ledger, channelId: 1, cost: 99, time.GetUtcNow());

        CandidatePlan plan = selector.Plan(Route(Channel(1, dailyLimit: 100)));

        Assert.Single(plan.Candidates);
        Assert.Empty(plan.Blocks);
        Assert.Null(plan.EarliestRecovery);
    }

    [Fact]
    public void OutsideItsTimeWindowAChannelRecoversAtTheNextOpening()
    {
        (CandidateSelector selector, _, _, _) = Create();

        // 周五 13:00–14:00（UTC），现在是 12:00。
        var window = new TimeWindow(1 << (int)DayOfWeek.Friday, 13 * 60, 14 * 60);
        CandidatePlan plan = selector.Plan(Route(Channel(1, windows: [window])));

        Assert.Empty(plan.Candidates);
        Assert.Equal(BlockReason.OutsideTimeWindow, Assert.Single(plan.Blocks).Reason);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), plan.EarliestRecovery);
    }

    [Fact]
    public void AFullMinuteOnEveryKeyRecoversAtTheNextMinute()
    {
        (CandidateSelector selector, RestrictionChecker checker, _, _) = Create();
        ChannelKeySnapshot key = Key(10, rpm: 1);
        Assert.True(checker.Rates.TryTake(key.Fingerprint, 1));

        CandidatePlan plan = selector.Plan(Route(Channel(1, keys: key)));

        Assert.Empty(plan.Candidates);
        Assert.Equal(BlockReason.RequestsPerMinute, Assert.Single(plan.Blocks).Reason);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 1, 0, TimeSpan.Zero), plan.EarliestRecovery);
    }

    [Fact]
    public void OneKeyWithRoomKeepsTheCandidate()
    {
        (CandidateSelector selector, RestrictionChecker checker, _, _) = Create();
        ChannelKeySnapshot full = Key(10, rpm: 1);
        ChannelKeySnapshot free = Key(11);
        Assert.True(checker.Rates.TryTake(full.Fingerprint, 1));

        CandidatePlan plan = selector.Plan(Route(Channel(1, keys: [full, free])));

        Assert.Single(plan.Candidates);
        Assert.Empty(plan.Blocks);
    }

    [Fact]
    public void AConcurrencyBlockLeavesNoRecoveryTimeAtAll()
    {
        (CandidateSelector selector, RestrictionChecker checker, CostLedger ledger, FakeTimeProvider time) = Create();
        ChannelKeySnapshot busy = Key(10, concurrency: 1);
        Assert.NotNull(checker.Concurrency.ChannelKeys.TryAcquire(busy.Fingerprint, 1));

        // 另一个渠道到了每日上限，本来能算出恢复时刻；有一个卡在并发上，整体就不给 Retry-After（Q91）。
        RecordCost(ledger, channelId: 2, cost: 100, time.GetUtcNow());
        CandidatePlan plan = selector.Plan(Route(Channel(1, keys: busy), Channel(2, dailyLimit: 100)));

        Assert.Empty(plan.Candidates);
        Assert.Equal(2, plan.Blocks.Count);
        Assert.Contains(plan.Blocks, b => b.Reason == BlockReason.Concurrency);
        Assert.Null(plan.EarliestRecovery);
    }

    [Fact]
    public void AKeyWhitelistThatExcludesTheModelIsNeverARetryableBlock()
    {
        (CandidateSelector selector, _, _, _) = Create();

        CandidatePlan plan = selector.Plan(Route(Channel(1, keys: Key(10, allowedModels: "other-model"))));

        Assert.Empty(plan.Candidates);
        Assert.Equal(BlockReason.KeyModelWhitelist, Assert.Single(plan.Blocks).Reason);

        // 等下去也不会变好：配置问题，不给恢复时刻。
        Assert.Null(plan.EarliestRecovery);
        Assert.True(plan.HasRestriction);
    }

    [Fact]
    public void CooldownAndRestrictionTogetherTakeTheEarlierRecovery()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var zone = new GatewayTimeZone(time);
        var ledger = new CostLedger(zone);
        var cooldowns = new CooldownRegistry(time, () => 0.5);
        var checker = new RestrictionChecker(ledger, new RequestRateCounter(time), new ConcurrencyGauges(), zone, time);
        var selector = new CandidateSelector(cooldowns, new HealthTracker(time), checker);

        // 渠道 1 的密钥因为鉴权失败进了冷却，渠道 2 到了每日上限（要等到午夜）。
        CooldownPermit permit = cooldowns.TryAcquire(1, Model, "fp-10", out _)!;
        cooldowns.Complete(permit, 1, Model, "fp-10", FailureClass.AuthFailure, null);
        RecordCost(ledger, channelId: 2, cost: 100, time.GetUtcNow());

        CandidatePlan plan = selector.Plan(Route(Channel(1), Channel(2, dailyLimit: 100)));
        DateTimeOffset cooling = cooldowns.Active().Single().Until;

        Assert.Empty(plan.Candidates);
        Assert.False(plan.AllCooling);
        Assert.True(plan.HasRestriction);

        // 冷却比午夜早得多，取冷却那个。
        Assert.Equal(cooling, plan.EarliestRecovery);
    }
}
