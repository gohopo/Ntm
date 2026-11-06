using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Tests.Relay;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Traffic;

/// <summary>保留期清理（Q42）：只删过期的请求记录与它们的 Attempt，汇总不动。</summary>
public sealed class RetentionCleanerTests
{
    [Fact]
    public async Task ExpiredRequestsAndTheirAttemptsAreDeletedButAggregatesStay()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 默认保留 7 天：8 天前的要删，6 天前的留着。批次设得很小，确认会分多批删完。
        Guid[] old = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Guid fresh = Guid.NewGuid();
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            foreach (Guid id in old)
            {
                db.Requests.Add(Record(id, now.AddDays(-8)));
            }

            db.Requests.Add(Record(fresh, now.AddDays(-6)));
            db.HourlyAggregates.Add(new HourlyAggregate
            {
                HourUtc = now.AddDays(-30),
                ChannelId = 1,
                ChannelName = "a",
                PublishedModel = "gpt-4o",
                UpstreamModel = "gpt-4o",
                RequestCount = 5,
            });
            await db.SaveChangesAsync();
        }

        RetentionCleaner cleaner = host.Services.GetRequiredService<RetentionCleaner>();
        Assert.Equal(3, await cleaner.DeleteExpiredAsync(CancellationToken.None));

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            Assert.Equal([fresh], await db.Requests.Select(r => r.Id).ToListAsync());
            Assert.Equal(1, await db.Attempts.CountAsync());
            Assert.Equal(1, await db.HourlyAggregates.CountAsync());
        }

        // 再跑一次没东西可删。
        Assert.Equal(0, await cleaner.DeleteExpiredAsync(CancellationToken.None));
    }

    private static RequestRecord Record(Guid id, DateTimeOffset startedAt)
    {
        var record = new RequestRecord
        {
            Id = id,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(1),
            PublishedModel = "gpt-4o",
            InboundApiFormat = ApiFormat.OpenAiChat,
            RequestKind = RequestKind.Chat,
            IsStream = true,
            Status = RequestStatus.Succeeded,
            AttemptCount = 1,
            UsageSource = UsageSource.Upstream,
        };
        record.Attempts.Add(new AttemptRecord
        {
            Ordinal = 1,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(1),
            ChannelId = 1,
            ChannelName = "a",
            ChannelKeyId = 1,
            ChannelKeyFingerprint = "f",
            ChannelKeyPrefix = "sk-a",
            UpstreamApiFormat = ApiFormat.OpenAiChat,
            UpstreamModel = "gpt-4o",
            RequestUri = "https://a.example/v1/chat/completions",
        });
        return record;
    }
}
