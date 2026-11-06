using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Data;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Tests.Relay;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Restrictions;

/// <summary>
/// 费用上限的计数器（Q32/Q87/Q88）：渠道按网关时区的自然日，惰性地在新的一天归零；
/// API 密钥终身累计。启动时从汇总表载入。
/// </summary>
public sealed class CostLedgerTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static RequestRecord Spent(DateTimeOffset at, int channelId, long cost, int? apiKeyId = 7)
    {
        var record = new RequestRecord { PublishedModel = "m", StartedAt = at, ApiKeyId = apiKeyId };
        record.Attempts.Add(new AttemptRecord
        {
            StartedAt = at,
            ChannelId = channelId,
            ChannelName = "c",
            ChannelKeyFingerprint = "f",
            ChannelKeyPrefix = "p",
            UpstreamModel = "m",
            RequestUri = "https://c.example",
            CostNanoUsd = cost,
        });
        return record;
    }

    [Fact]
    public void AChannelsDailySpendStartsOverAtLocalMidnight()
    {
        // 上海 2026-10-30 23:30 = UTC 15:30。
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 15, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(Shanghai);
        var ledger = new CostLedger(new GatewayTimeZone(time));

        ledger.Record(Spent(time.GetUtcNow(), channelId: 1, cost: 500));
        Assert.Equal(500, ledger.ChannelToday(1));

        // 过了上海的午夜（UTC 还是 10-30），渠道归零，API 密钥不归零。
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, ledger.ChannelToday(1));
        Assert.Equal(500, ledger.ApiKeyTotal(7));

        ledger.Record(Spent(time.GetUtcNow(), channelId: 1, cost: 200));
        Assert.Equal(200, ledger.ChannelToday(1));
        Assert.Equal(700, ledger.ApiKeyTotal(7));
    }

    [Fact]
    public void ARequestThatStartedYesterdayDoesNotCountTowardsToday()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 16, 5, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(Shanghai);
        var ledger = new CostLedger(new GatewayTimeZone(time));

        // 上海 10-30 23:58 开始的 Attempt 在午夜后才结束：算在开始那天，和汇总表的归集一致。
        ledger.Record(Spent(new DateTimeOffset(2026, 10, 30, 15, 58, 0, TimeSpan.Zero), channelId: 1, cost: 300));

        Assert.Equal(0, ledger.ChannelToday(1));
        Assert.Equal(300, ledger.ApiKeyTotal(7));
    }

    [Fact]
    public void UnpricedAttemptsAndRequestsWithoutAnApiKeyAreSkipped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 4, 0, 0, TimeSpan.Zero));
        var ledger = new CostLedger(new GatewayTimeZone(time));

        RequestRecord unpriced = Spent(time.GetUtcNow(), channelId: 1, cost: 0);
        unpriced.Attempts[0].CostNanoUsd = null;
        ledger.Record(unpriced);
        ledger.Record(Spent(time.GetUtcNow(), channelId: 2, cost: 40, apiKeyId: null));

        Assert.Equal(0, ledger.ChannelToday(1));
        Assert.Equal(40, ledger.ChannelToday(2));
        Assert.Equal(0, ledger.ApiKeyTotal(7));
    }

    [Fact]
    public void ForgettingDropsTheCounters()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 4, 0, 0, TimeSpan.Zero));
        var ledger = new CostLedger(new GatewayTimeZone(time));
        ledger.Record(Spent(time.GetUtcNow(), channelId: 1, cost: 10));

        ledger.ForgetChannel(1);
        ledger.ForgetApiKey(7);

        Assert.Equal(0, ledger.ChannelToday(1));
        Assert.Equal(0, ledger.ApiKeyTotal(7));
    }

    [Fact]
    public async Task LoadingSumsTodayForChannelsAndEverythingForApiKeys()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        GatewayTimeZone zone = host.Services.GetRequiredService<GatewayTimeZone>();
        DateTimeOffset midnight = zone.StartOf(zone.Today());
        DateTimeOffset thisHour = HourOf(midnight);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            db.HourlyAggregates.AddRange(
                Aggregate(thisHour, channelId: 1, apiKeyId: 3, cost: 100),
                Aggregate(thisHour.AddHours(1), channelId: 1, apiKeyId: 0, cost: 20),

                // 昨天的：渠道不算，API 密钥照算。
                Aggregate(thisHour.AddHours(-2), channelId: 1, apiKeyId: 3, cost: 5_000),

                // 渠道 0 是零 Attempt 的请求，没有花费可言，也不该被当成一个渠道。
                Aggregate(thisHour, channelId: 0, apiKeyId: 3, cost: 0));
            await db.SaveChangesAsync();
        }

        var ledger = new CostLedger(zone, host.Services.GetRequiredService<IServiceScopeFactory>());
        await ledger.LoadAsync();

        Assert.Equal(120, ledger.ChannelToday(1));
        Assert.Equal(0, ledger.ChannelToday(0));
        Assert.Equal(5_100, ledger.ApiKeyTotal(3));
    }

    private static DateTimeOffset HourOf(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, TimeSpan.Zero);

    private static HourlyAggregate Aggregate(DateTimeOffset hour, int channelId, int apiKeyId, long cost) => new()
    {
        HourUtc = hour,
        ChannelId = channelId,
        ChannelName = "c" + channelId,
        PublishedModel = "m",
        UpstreamModel = "m" + hour.Hour,
        ApiKeyId = apiKeyId,
        CostNanoUsd = cost,
    };
}
