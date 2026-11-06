using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Routing;

public class CandidateSelectorTests
{
    private const string Model = "gpt-4o";

    private static ChannelSnapshot Channel(int id, ApiFormat format = ApiFormat.OpenAiChat, params string[] secrets)
    {
        string[] keys = secrets.Length == 0 ? ["secret-" + id] : secrets;
        return new ChannelSnapshot
        {
            ChannelId = id,
            ChannelName = "ch" + id,
            BaseUrl = $"https://ch{id}.example",
            ApiFormat = format,
            Keys = [.. keys.Select((s, i) => new ChannelKeySnapshot
            {
                ChannelKeyId = (id * 10) + i,
                Name = "key" + i,
                Secret = s,
                Fingerprint = "fp-" + s,
                Prefix = s[..Math.Min(4, s.Length)],
            })],
            UpstreamModels = [Model],
        };
    }

    private static PublishedModelRoute Route(params (int Priority, ChannelSnapshot Channel, int Weight)[] candidates)
    {
        var groups = candidates
            .GroupBy(c => c.Priority)
            .OrderByDescending(g => g.Key)
            .Select(g => new PriorityGroup
            {
                Priority = g.Key,
                Candidates = [.. g.Select(c => new RouteCandidate
                {
                    Channel = c.Channel,
                    UpstreamModel = Model,
                    Priority = c.Priority,
                    Weight = c.Weight,
                })],
            })
            .ToList();

        return new PublishedModelRoute
        {
            PublishedModelId = 1,
            Name = "published",
            RequestKind = RequestKind.Chat,
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            PriorityGroups = groups,
        };
    }

    private static (CandidateSelector Selector, CooldownRegistry Cooldowns, HealthTracker Health, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cooldowns = new CooldownRegistry(time, () => 0.5);
        var health = new HealthTracker(time);
        return (new CandidateSelector(cooldowns, health), cooldowns, health, time);
    }

    private static void Cool(CooldownRegistry cooldowns, ChannelSnapshot channel, FailureClass failure)
    {
        string fingerprint = channel.Keys[0].Fingerprint;
        CooldownPermit permit = cooldowns.TryAcquire(channel.ChannelId, Model, fingerprint, out _)!;
        cooldowns.Complete(permit, channel.ChannelId, Model, fingerprint, failure, null);
    }

    [Fact]
    public void EveryCandidateIsPlannedWhenNothingIsCooling()
    {
        (CandidateSelector selector, _, _, _) = Create();
        PublishedModelRoute route = Route((10, Channel(1), 1), (10, Channel(2), 1));

        CandidatePlan plan = selector.Plan(route);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Null(plan.EarliestRecovery);
        Assert.False(plan.AllCooling);
    }

    [Fact]
    public void HigherPriorityTiersComeFirstAndNeverMix()
    {
        // 档与档之间不混流：高档的候选全部排在低档前面，低档只是故障转移的后备。
        (CandidateSelector selector, _, _, _) = Create();
        PublishedModelRoute route = Route((10, Channel(1), 1), (10, Channel(2), 1), (5, Channel(3), 100));

        CandidatePlan plan = selector.Plan(route);

        Assert.Equal(3, plan.Candidates.Count);
        Assert.Equal(3, plan.Candidates[2].ChannelId);
        Assert.True(plan.Candidates[0].ChannelId is 1 or 2);
    }

    [Fact]
    public void WeightDecidesWhoLeadsWithinATier()
    {
        // 档内按权重分流：3:1 的权重在四轮里应该是三次和一次当头名。
        (CandidateSelector selector, _, _, _) = Create();
        ChannelSnapshot heavy = Channel(1);
        ChannelSnapshot light = Channel(2);
        PublishedModelRoute route = Route((10, heavy, 3), (10, light, 1));

        var leads = new Dictionary<int, int> { [1] = 0, [2] = 0 };
        for (int i = 0; i < 4; i++)
        {
            leads[selector.Plan(route).Candidates[0].ChannelId]++;
        }

        Assert.Equal(3, leads[1]);
        Assert.Equal(1, leads[2]);
    }

    [Fact]
    public void TheRestOfATierKeepsTheMatchersOrderBehindTheLeader()
    {
        // 轮询只决定头名，后面保持匹配给出的确定顺序——那就是故障转移的顺序。
        (CandidateSelector selector, _, _, _) = Create();
        PublishedModelRoute route = Route((10, Channel(1), 1), (10, Channel(2), 1), (10, Channel(3), 1));

        CandidatePlan plan = selector.Plan(route);

        Assert.Equal(3, plan.Candidates.Count);
        int[] trailing = [.. plan.Candidates.Skip(1).Select(c => c.ChannelId)];
        Assert.Equal([.. trailing.OrderBy(id => id)], trailing);
    }

    [Fact]
    public void AHealthierCandidateLeadsMoreOften()
    {
        // 健康度只在档内调权重：同权重下，近期失败多的那个少当头名。
        (CandidateSelector selector, _, HealthTracker health, _) = Create();
        ChannelSnapshot good = Channel(1);
        ChannelSnapshot bad = Channel(2);
        PublishedModelRoute route = Route((10, good, 1), (10, bad, 1));

        for (int i = 0; i < 20; i++)
        {
            health.Record(1, Model, FailureClass.None);
            health.Record(2, Model, FailureClass.UpstreamServerError);
        }

        var leads = new Dictionary<int, int> { [1] = 0, [2] = 0 };
        for (int i = 0; i < 20; i++)
        {
            leads[selector.Plan(route).Candidates[0].ChannelId]++;
        }

        Assert.True(leads[1] > leads[2], $"健康的渠道应该更常当头名，实际 {leads[1]}:{leads[2]}");
        Assert.True(leads[2] > 0, "不健康的渠道仍该偶尔被选到，否则它没有机会恢复");
    }

    [Fact]
    public void ACoolingCandidateIsSkippedButTheRestStillServe()
    {
        (CandidateSelector selector, CooldownRegistry cooldowns, _, _) = Create();
        ChannelSnapshot cooling = Channel(1);
        PublishedModelRoute route = Route((10, cooling, 1), (10, Channel(2), 1));

        Cool(cooldowns, cooling, FailureClass.UpstreamOverloaded);
        CandidatePlan plan = selector.Plan(route);

        Assert.Single(plan.Candidates);
        Assert.Equal(2, plan.Candidates[0].ChannelId);

        // 有候选可用时不报恢复时间：客户端不需要等。
        Assert.Null(plan.EarliestRecovery);
        Assert.False(plan.AllCooling);
    }

    [Fact]
    public void EverythingCoolingReportsTheEarliestRecovery()
    {
        (CandidateSelector selector, CooldownRegistry cooldowns, _, FakeTimeProvider time) = Create();
        ChannelSnapshot first = Channel(1);
        ChannelSnapshot second = Channel(2);
        PublishedModelRoute route = Route((10, first, 1), (10, second, 1));

        // 渠道 1 连接失败冷却 2 分钟；渠道 2 过载冷却 1 分钟。
        Cool(cooldowns, first, FailureClass.ConnectionFailure);
        Cool(cooldowns, second, FailureClass.UpstreamOverloaded);

        CandidatePlan plan = selector.Plan(route);

        Assert.Empty(plan.Candidates);
        Assert.True(plan.AllCooling);
        Assert.Equal(time.GetUtcNow().AddMinutes(1), plan.EarliestRecovery);
    }

    [Fact]
    public void ACandidateWhoseEveryKeyIsCoolingIsSkipped()
    {
        // 「所有密钥都在冷却就等于整个渠道不可用」是在选路时算出来的，不存状态（Q28）。
        (CandidateSelector selector, CooldownRegistry cooldowns, _, _) = Create();
        ChannelSnapshot twoKeys = Channel(1, ApiFormat.OpenAiChat, "s1", "s2");
        PublishedModelRoute route = Route((10, twoKeys, 1));

        CooldownPermit first = cooldowns.TryAcquire(1, Model, "fp-s1", out _)!;
        cooldowns.Complete(first, 1, Model, "fp-s1", FailureClass.AuthFailure, null);

        // 还有一把好密钥：候选仍然可用。
        Assert.Single(selector.Plan(route).Candidates);

        CooldownPermit second = cooldowns.TryAcquire(1, Model, "fp-s2", out _)!;
        cooldowns.Complete(second, 1, Model, "fp-s2", FailureClass.AuthFailure, null);

        CandidatePlan plan = selector.Plan(route);
        Assert.Empty(plan.Candidates);
        Assert.True(plan.AllCooling);
    }

    [Fact]
    public void TheFilterRemovesCandidatesBeforeCooldownIsConsidered()
    {
        // 入站格式不被这个上游支持时，候选根本不该进来。
        (CandidateSelector selector, _, _, _) = Create();
        PublishedModelRoute route = Route(
            (10, Channel(1, ApiFormat.Anthropic), 1),
            (10, Channel(2, ApiFormat.OpenAiChat), 1));

        CandidatePlan plan = selector.Plan(route, c => c.Channel.ApiFormat == ApiFormat.OpenAiChat);

        Assert.Single(plan.Candidates);
        Assert.Equal(2, plan.Candidates[0].ChannelId);

        // 被过滤掉不是「在冷却」：不该让客户端以为等一会儿就好。
        Assert.Null(plan.EarliestRecovery);
    }

    [Fact]
    public void EverythingFilteredOutIsNotCooling()
    {
        (CandidateSelector selector, _, _, _) = Create();
        PublishedModelRoute route = Route((10, Channel(1, ApiFormat.Anthropic), 1));

        CandidatePlan plan = selector.Plan(route, c => c.Channel.ApiFormat == ApiFormat.OpenAiChat);

        Assert.Empty(plan.Candidates);
        Assert.False(plan.AllCooling);
        Assert.Null(plan.EarliestRecovery);
    }

    [Fact]
    public void ARouteWithNoCandidatesPlansNothing()
    {
        (CandidateSelector selector, _, _, _) = Create();

        CandidatePlan plan = selector.Plan(Route());

        Assert.Empty(plan.Candidates);
        Assert.False(plan.AllCooling);
    }

    [Fact]
    public void ACoolingHighTierFallsThroughToTheLowerTier()
    {
        (CandidateSelector selector, CooldownRegistry cooldowns, _, _) = Create();
        ChannelSnapshot primary = Channel(1);
        PublishedModelRoute route = Route((10, primary, 1), (5, Channel(2), 1));

        Cool(cooldowns, primary, FailureClass.ConnectionFailure);
        CandidatePlan plan = selector.Plan(route);

        Assert.Single(plan.Candidates);
        Assert.Equal(2, plan.Candidates[0].ChannelId);
    }

    [Fact]
    public void RecoveredCandidatesComeBack()
    {
        (CandidateSelector selector, CooldownRegistry cooldowns, _, FakeTimeProvider time) = Create();
        ChannelSnapshot only = Channel(1);
        PublishedModelRoute route = Route((10, only, 1));

        Cool(cooldowns, only, FailureClass.UpstreamOverloaded);
        Assert.Empty(selector.Plan(route).Candidates);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Single(selector.Plan(route).Candidates);
    }

    [Fact]
    public void APinnedCandidateGoesFirstWithoutAdvancingTheRoundRobin()
    {
        (CandidateSelector selector, _, _, _) = Create();
        ChannelSnapshot a = Channel(1);
        ChannelSnapshot b = Channel(2);
        PublishedModelRoute route = Route((0, a, 1), (0, b, 1));
        var pin = new AffinityPin(2, Model, b.Keys[0].Fingerprint);

        CandidatePlan pinned = selector.Plan(route, pin: pin);
        Assert.True(pinned.PinApplied);
        Assert.Equal([2, 1], pinned.Candidates.Select(c => c.Channel.ChannelId));

        // 轮询没被推进：没有 pin 的请求照常从第一个开始轮。
        Assert.Equal(1, selector.Plan(route).Candidates[0].Channel.ChannelId);
        Assert.Equal(2, selector.Plan(route).Candidates[0].Channel.ChannelId);
    }

    [Fact]
    public void APinOutsideTheHighestUsableGroupIsIgnored()
    {
        (CandidateSelector selector, _, _, _) = Create();
        ChannelSnapshot main = Channel(1);
        ChannelSnapshot backup = Channel(2);
        PublishedModelRoute route = Route((10, main, 1), (0, backup, 1));

        CandidatePlan plan = selector.Plan(route, pin: new AffinityPin(2, Model, backup.Keys[0].Fingerprint));

        Assert.False(plan.PinApplied);
        Assert.Equal([1, 2], plan.Candidates.Select(c => c.Channel.ChannelId));
    }

    [Fact]
    public void APinOnACoolingCandidateIsIgnored()
    {
        (CandidateSelector selector, CooldownRegistry cooldowns, _, _) = Create();
        ChannelSnapshot a = Channel(1);
        ChannelSnapshot b = Channel(2);
        Cool(cooldowns, a, FailureClass.AuthFailure);

        CandidatePlan plan = selector.Plan(Route((0, a, 1), (0, b, 1)), pin: new AffinityPin(1, Model, a.Keys[0].Fingerprint));

        Assert.False(plan.PinApplied);
        Assert.Equal([2], plan.Candidates.Select(c => c.Channel.ChannelId));
    }

    [Fact]
    public void InspectReportsBlocksAndHealthWithoutAdvancingTheRoundRobin()
    {
        // 「现在的选路」只读（Q259）：看多少次，之后的轮询顺序都和一个从没被看过的同样状态的选择器一致。
        (CandidateSelector watched, CooldownRegistry watchedCooldowns, HealthTracker watchedHealth, _) = Create();
        (CandidateSelector replay, CooldownRegistry replayCooldowns, HealthTracker replayHealth, _) = Create();
        ChannelSnapshot first = Channel(1);
        ChannelSnapshot second = Channel(2);
        ChannelSnapshot third = Channel(3);
        PublishedModelRoute watchedRoute = Route((10, first, 2), (10, second, 1), (5, third, 1));
        PublishedModelRoute replayRoute = Route((10, first, 2), (10, second, 1), (5, third, 1));

        foreach ((CooldownRegistry cooldowns, HealthTracker health) in new[] { (watchedCooldowns, watchedHealth), (replayCooldowns, replayHealth) })
        {
            Cool(cooldowns, third, FailureClass.UpstreamOverloaded);
            for (int i = 0; i < 3; i++)
            {
                health.Record(2, Model, FailureClass.UpstreamServerError);
            }
        }

        IReadOnlyList<CandidateState> states = watched.Inspect(watchedRoute);
        Assert.Equal([1, 2, 3], states.Select(s => s.Candidate.ChannelId));
        Assert.Null(states[0].Block);
        Assert.Equal((0, 0, 1.0), (states[0].Health.Successes, states[0].Health.Failures, states[0].Health.Factor));
        Assert.Equal(200, states[0].EffectiveWeight);
        Assert.Equal(3, states[1].Health.Failures);
        Assert.True(states[1].Health.Factor < 1);
        Assert.Equal(BlockReason.Cooldown, states[2].Block?.Reason);

        var watchedLeads = new List<int>();
        var replayLeads = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            _ = watched.Inspect(watchedRoute);
            _ = watched.Inspect(watchedRoute);
            watchedLeads.Add(watched.Plan(watchedRoute).Candidates[0].ChannelId);
            replayLeads.Add(replay.Plan(replayRoute).Candidates[0].ChannelId);
        }

        Assert.Equal(replayLeads, watchedLeads);
    }

    [Fact]
    public void InspectDoesNotClaimTheProbeOfAnExpiredCooldown()
    {
        // 冷却到期后只有一个请求能去探测；后台看一眼不能把这个名额占掉（Q259）。
        (CandidateSelector selector, CooldownRegistry cooldowns, _, FakeTimeProvider time) = Create();
        ChannelSnapshot channel = Channel(1);
        PublishedModelRoute route = Route((10, channel, 1));

        Cool(cooldowns, channel, FailureClass.UpstreamOverloaded);
        Assert.Equal(BlockReason.Cooldown, selector.Inspect(route)[0].Block?.Reason);

        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(selector.Inspect(route)[0].Block);
        Assert.Null(selector.Inspect(route)[0].Block);

        CooldownPermit? permit = cooldowns.TryAcquire(1, Model, channel.Keys[0].Fingerprint, out _);
        Assert.NotNull(permit);
    }
}
