using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Tests.Relay;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Sync;

public sealed class ModelSyncServiceTests
{
    private static readonly (string Name, int Priority, string[] Secrets)[] OneChannel = [("a", 0, ["sk-a1", "sk-a2"])];

    [Fact]
    public async Task SyncReplacesSyncedEntriesAndKeepsManualOnes()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => List("manual-1", "synced-keep", "new-model"));

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Removed);
        Assert.Equal("上游列出 3 个模型，新增 1 个，移除 1 个。", outcome.Message);

        List<UpstreamModel> models = await ReadModelsAsync(host);
        Assert.Equal(["manual-1", "manual-2", "new-model", "synced-keep"], models.Select(m => m.Name).Order(StringComparer.Ordinal));

        // 上游也列了的人工条目仍算人工录入，只是记下见过（Q138）。
        UpstreamModel manual = models.Single(m => m.Name == "manual-1");
        Assert.Equal(UpstreamModelSource.Manual, manual.Source);
        Assert.NotNull(manual.LastSeenAt);
        Assert.Null(models.Single(m => m.Name == "manual-2").LastSeenAt);
        Assert.Equal(UpstreamModelSource.Synced, models.Single(m => m.Name == "new-model").Source);

        Channel channel = await ReadChannelAsync(host);
        Assert.True(channel.LastModelSyncSucceeded);
        Assert.Equal(1, channel.LastModelSyncAdded);
        Assert.Equal(outcome.Message, channel.LastModelSyncMessage);
    }

    [Fact]
    public async Task ANewModelMatchingAnExistingRuleEntersRoutingAndIsRecorded()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => List("manual-1", "synced-keep", "synced-gone", "gpt-5-turbo", "other"));

        await SyncAsync(host);

        // 快照已经重建：新模型能被 gpt-5 系列的规则选中（Q142）。
        PublishedModelRoute route = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.FindRoute("gpt-5")!;
        Assert.Contains(route.PriorityGroups.SelectMany(g => g.Candidates), c => c.UpstreamModel == "gpt-5-turbo");

        List<ModelSyncEvent> events = await ReadEventsAsync(host);
        ModelSyncEvent entered = Assert.Single(events);
        Assert.Equal(ModelSyncEventKind.EnteredRouting, entered.Kind);
        Assert.Equal("gpt-5-turbo", entered.UpstreamModel);
        Assert.Equal("gpt-5", entered.PublishedModels);
        Assert.Equal("a", entered.ChannelName);
        Assert.False(entered.Acknowledged);
    }

    [Fact]
    public async Task ARemovedModelThatWasInRoutingIsRecordedAsLeaving()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => List("manual-1", "synced-keep"));

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.Equal(1, outcome.Removed);
        ModelSyncEvent left = Assert.Single(await ReadEventsAsync(host));
        Assert.Equal(ModelSyncEventKind.LeftRouting, left.Kind);
        Assert.Equal("synced-gone", left.UpstreamModel);
        Assert.Equal("gone", left.PublishedModels);
    }

    [Fact]
    public async Task AnUnchangedListDoesNotRebuildTheSnapshot()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => List("manual-1", "synced-keep", "synced-gone"));
        ConfigSnapshot before = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.True(outcome.Succeeded);
        Assert.Equal("上游列出 3 个模型，清单没有变化。", outcome.Message);
        Assert.Same(before, host.Services.GetRequiredService<ConfigSnapshotProvider>().Current);
        Assert.Empty(await ReadEventsAsync(host));
    }

    [Fact]
    public async Task AFailingKeyFallsBackToTheNextOne()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => ScriptedUpstream.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"revoked"}}"""));
        host.Upstream.Script("sk-a2", () => List("manual-1", "synced-keep", "synced-gone"));

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["sk-a1", "sk-a2"], host.Upstream.SecretsTried);

        // 同步失败不进 Cooldown（Q136）。
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task CoolingKeysAreSkipped()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a2", () => List("manual-1", "synced-keep", "synced-gone"));
        CooldownRegistry cooldowns = host.Services.GetRequiredService<CooldownRegistry>();
        string fingerprint = SecretFingerprint.Compute("sk-a1");
        CooldownPermit permit = cooldowns.TryAcquire(1, "x", fingerprint, out _)!;
        cooldowns.Complete(permit, 1, "x", fingerprint, FailureClass.AuthFailure, null);

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["sk-a2"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task AFailureKeepsTheListAndIsRecordedOnTheChannel()
    {
        await using RelayHost host = await StartAsync();
        host.Upstream.Script("sk-a1", () => ScriptedUpstream.Json(HttpStatusCode.OK, """{"data":[]}"""));
        host.Upstream.Script("sk-a2", () => ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "oops"));

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.False(outcome.Succeeded);
        // 每把密钥一段，前面是密钥前缀，用「；」隔开。
        string[] parts = outcome.Message.Split('；');
        Assert.Equal(2, parts.Length);
        Assert.Contains("…：上游返回了一个空的模型清单", parts[0], StringComparison.Ordinal);
        Assert.Contains("…：上游返回 HTTP 500", parts[1], StringComparison.Ordinal);
        Assert.Equal(4, (await ReadModelsAsync(host)).Count);

        Channel channel = await ReadChannelAsync(host);
        Assert.False(channel.LastModelSyncSucceeded);
        Assert.Equal(outcome.Message, channel.LastModelSyncMessage);
    }

    [Fact]
    public async Task AChannelWithoutEnabledKeysFailsWithoutCallingUpstream()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, [])], (_, _) => { });

        ModelSyncOutcome outcome = await SyncAsync(host);

        Assert.False(outcome.Succeeded);
        Assert.Equal("这个渠道没有启用的渠道密钥。", outcome.Message);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task AnUnknownChannelReturnsNull()
    {
        await using RelayHost host = await StartAsync();
        Assert.Null(await host.Services.GetRequiredService<ModelSyncService>().SyncAsync(999, CancellationToken.None));
    }

    /// <summary>
    /// 渠道 a：人工录入 manual-1、manual-2，同步来的 synced-keep、synced-gone。
    /// 下发模型 gpt-5 用正则 gpt-5.* 选中，gone 精确选中 synced-gone。
    /// </summary>
    private static Task<RelayHost> StartAsync() => RelayHost.StartWithModelsAsync(OneChannel, (db, channels) =>
    {
        Channel channel = channels[0];
        channel.UpstreamModels.Add(new UpstreamModel { Name = "manual-1", Source = UpstreamModelSource.Manual });
        channel.UpstreamModels.Add(new UpstreamModel { Name = "manual-2", Source = UpstreamModelSource.Manual });
        channel.UpstreamModels.Add(new UpstreamModel { Name = "synced-keep", Source = UpstreamModelSource.Synced });
        channel.UpstreamModels.Add(new UpstreamModel { Name = "synced-gone", Source = UpstreamModelSource.Synced });

        var series = new PublishedModel { Name = "gpt-5", Enabled = true };
        series.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Regex, Pattern = "gpt-5.*" });
        var gone = new PublishedModel { Name = "gone", Enabled = true };
        gone.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = "synced-gone" });
        db.PublishedModels.AddRange(series, gone);
    });

    private static async Task<ModelSyncOutcome> SyncAsync(RelayHost host)
    {
        int id = (await ReadChannelAsync(host)).Id;
        ModelSyncOutcome? outcome = await host.Services.GetRequiredService<ModelSyncService>().SyncAsync(id, CancellationToken.None);
        return outcome!;
    }

    private static HttpResponseMessage List(params string[] ids) =>
        ScriptedUpstream.Json(HttpStatusCode.OK, "{\"data\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\"}}")) + "]}");

    private static async Task<Channel> ReadChannelAsync(RelayHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Channels.AsNoTracking().SingleAsync();
    }

    private static async Task<List<UpstreamModel>> ReadModelsAsync(RelayHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().UpstreamModels.AsNoTracking().ToListAsync();
    }

    private static async Task<List<ModelSyncEvent>> ReadEventsAsync(RelayHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().ModelSyncEvents.AsNoTracking().ToListAsync();
    }
}
