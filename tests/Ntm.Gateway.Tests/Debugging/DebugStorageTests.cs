using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Debugging;
using Ntm.Gateway.Tests.Relay;

namespace Ntm.Gateway.Tests.Debugging;

/// <summary>落盘队列满了丢新的并记数（Q160）；清理按保留期删原文，顺手清掉到期的开关（Q155/Q159）。</summary>
public sealed class DebugStorageTests
{
    private static DebugPayload Payload(DateTimeOffset at) => new()
    {
        RequestId = Guid.CreateVersion7(),
        AttemptOrdinal = 1,
        Kind = DebugPayloadKind.ClientRequest,
        CapturedAt = at,
        Headers = string.Empty,
        Body = "{}",
    };

    [Fact]
    public void AFullQueueDropsNewPayloadsAndCountsThem()
    {
        // 不启动后台写入，队列只进不出。
        using var sink = new DebugCaptureSink(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DebugCaptureOptions { QueueCapacity = 2 }),
            NullLogger<DebugCaptureSink>.Instance);

        for (int i = 0; i < 5; i++)
        {
            sink.Enqueue(Payload(DateTimeOffset.UtcNow));
        }

        Assert.Equal(3, sink.Dropped);
    }

    [Fact]
    public async Task TheCleanerDeletesOldPayloadsAndClearsExpiredSwitches()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"]), ("b", 0, ["sk-b"])],
            (db, channels) =>
            {
                channels[0].DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(-5);
                channels[1].DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                db.ApiKeys.Local.Single().DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
                db.Settings.Add(new GatewaySettings { DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(-1) });
            });

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            DebugDbContext debug = scope.ServiceProvider.GetRequiredService<DebugDbContext>();
            debug.DebugPayloads.AddRange(
                Payload(DateTimeOffset.UtcNow.AddHours(-25)),
                Payload(DateTimeOffset.UtcNow.AddHours(-30)),
                Payload(DateTimeOffset.UtcNow.AddHours(-1)));
            await debug.SaveChangesAsync();
        }

        int deleted = await host.Services.GetRequiredService<DebugCleaner>().DeleteExpiredAsync(CancellationToken.None);
        Assert.Equal(2, deleted);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            DebugDbContext debug = scope.ServiceProvider.GetRequiredService<DebugDbContext>();
            DebugPayload kept = Assert.Single(await debug.DebugPayloads.AsNoTracking().ToListAsync());
            Assert.True(kept.CapturedAt > DateTimeOffset.UtcNow.AddHours(-2));

            ConfigDbContext config = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            List<Channel> channels = await config.Channels.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
            Assert.Null(channels[0].DebugCaptureUntil);
            Assert.NotNull(channels[1].DebugCaptureUntil);
            Assert.Null((await config.ApiKeys.AsNoTracking().SingleAsync()).DebugCaptureUntil);
            Assert.Null((await config.Settings.AsNoTracking().SingleAsync()).DebugCaptureUntil);
        }
    }
}
