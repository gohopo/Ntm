using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Routing;

namespace Ntm.Gateway.Tests.Routing;

/// <summary>Affinity 表（Q121）：空闲 1 小时过期，命中续期，超过上限先清过期的再清最久没用的。</summary>
public class AffinityTableTests
{
    private static readonly AffinityPin Pin = new(1, "gpt-4o", "fp");

    [Fact]
    public void AnEntryExpiresAfterAnHourOfIdleness()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var table = new AffinityTable(time);
        table.Remember(1, Pin);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(Pin, table.Find(1));

        // 成功落点会再 Remember 一次，空闲时间从头算。
        table.Remember(1, Pin);
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(Pin, table.Find(1));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(table.Find(1));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void OverTheCapTheLeastRecentlyUsedAreDropped()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var table = new AffinityTable(time, maxEntries: 10);
        for (int i = 0; i < 11; i++)
        {
            table.Remember((UInt128)i, Pin);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // 清到上限的 90%：最早的两条没了，最新的还在。
        Assert.Equal(9, table.Count);
        Assert.Null(table.Find(0));
        Assert.Null(table.Find(1));
        Assert.Equal(Pin, table.Find(10));
    }
}
