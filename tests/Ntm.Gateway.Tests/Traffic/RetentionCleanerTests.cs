using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Audit;
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

    [Fact]
    public async Task AuditRetentionDefaultsTo180DaysAndDoesNotDeleteRequestsOrRecentAudit()
    {
        Assert.Equal(180, new RetentionOptions().AuditRetentionDays);
        await using AuditHarness h = await AuditHarness.StartAsync();
        DateTimeOffset cutoff = h.Time.GetUtcNow().AddDays(-180);
        await using (AsyncServiceScope scope = h.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            db.AuditEntries.AddRange(
                new AuditEntry { Action = "expired", OccurredAt = cutoff.AddMilliseconds(-1) },
                new AuditEntry { Action = "boundary", OccurredAt = cutoff },
                new AuditEntry { Action = "recent", OccurredAt = h.Time.GetUtcNow().AddDays(-8) });
            db.Requests.Add(Record(Guid.NewGuid(), cutoff.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await h.Cleaner.DeleteExpiredAuditAsync(CancellationToken.None));
        await using (AsyncServiceScope scope = h.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            Assert.Equal(["boundary", "recent"], await db.AuditEntries.OrderBy(e => e.Action).Select(e => e.Action).ToListAsync());
            Assert.Equal(1, await db.Requests.CountAsync());
            Assert.Equal(1, await db.Attempts.CountAsync());
        }

        Assert.Equal(0, await h.Cleaner.DeleteExpiredAuditAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("30", 1)]
    public async Task AuditRetentionConfigurationCanKeepRecordsForeverOrShortenRetention(string days, int deleted)
    {
        await using AuditHarness h = await AuditHarness.StartAsync(days);
        await using (AsyncServiceScope scope = h.Services.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            db.AuditEntries.Add(new AuditEntry { Action = "old", OccurredAt = h.Time.GetUtcNow().AddDays(-365) });
            await db.SaveChangesAsync();
        }

        Assert.Equal(deleted, await h.Cleaner.DeleteExpiredAuditAsync(CancellationToken.None));
        await using AsyncServiceScope read = h.Services.CreateAsyncScope();
        Assert.Equal(1 - deleted, await read.ServiceProvider.GetRequiredService<TrafficDbContext>().AuditEntries.CountAsync());
    }

    private sealed class AuditHarness(SqliteConnection connection, ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));

        public RetentionCleaner Cleaner => new(
            Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<IOptions<RetentionOptions>>(),
            Time,
            NullLogger<RetentionCleaner>.Instance);

        public static async Task<AuditHarness> StartAsync(string? days = null)
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
                days is null ? [] : new Dictionary<string, string?> { ["Ntm:Traffic:AuditRetentionDays"] = days }).Build();
            var services = new ServiceCollection();
            services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
            services.AddDbContext<TrafficDbContext>(options => options.UseSqlite(connection));
            var harness = new AuditHarness(connection, services.BuildServiceProvider());
            await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TrafficDbContext>().Database.MigrateAsync();
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
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
