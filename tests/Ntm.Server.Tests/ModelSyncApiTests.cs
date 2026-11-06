using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Sync;

namespace Ntm.Server.Tests;

/// <summary>ModelSync 的管理接口（Q135、Q140）：渠道上的开关与最近结果、「立即同步」、事件清单与「知道了」。</summary>
public sealed class ModelSyncApiTests
{
    private static JsonElement Sync(JsonElement detail) => detail.GetProperty("channel").GetProperty("modelSync");

    [Fact]
    public async Task NewChannelsHaveSyncOnAndTheSwitchCanBeTurnedOff()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("relay");

        JsonElement sync = Sync(await admin.GetJsonAsync(AdminClient.Channel(id)));
        Assert.True(sync.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sync.GetProperty("lastAt").ValueKind);

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "relay",
            baseUrl = "https://relay.example/v1",
            provider = "openai",
            modelSyncEnabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Sync(saved).GetProperty("enabled").GetBoolean());

        // 不给 modelSyncEnabled 就不动。
        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "relay",
            baseUrl = "https://relay.example/v1",
            provider = "openai",
        });
        Assert.False(Sync(kept).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task SyncNowReturnsTheOutcomeAndRecordsItOnTheChannel()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("relay");

        (HttpStatusCode status, JsonElement outcome) = await admin.PostAsync(AdminClient.Channel(id) + "/sync", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(outcome.GetProperty("lastSucceeded").GetBoolean());
        Assert.Equal("这个渠道没有启用的渠道密钥。", outcome.GetProperty("lastMessage").GetString());

        JsonElement recorded = Sync(await admin.GetJsonAsync(AdminClient.Channel(id)));
        Assert.False(recorded.GetProperty("lastSucceeded").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, recorded.GetProperty("lastAt").ValueKind);

        (HttpStatusCode missing, _) = await admin.PostAsync(AdminClient.Channel(999) + "/sync", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task EventsAreListedNewestFirstAndAcknowledgedTogether()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (IServiceScope scope = host.Services.CreateScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            db.ModelSyncEvents.AddRange(
                Event(ModelSyncEventKind.EnteredRouting, "gpt-5-turbo", now.AddMinutes(-2)),
                Event(ModelSyncEventKind.LeftRouting, "gpt-4-old", now.AddMinutes(-1)),
                Event(ModelSyncEventKind.EnteredRouting, "gpt-5-mini", now));
            await db.SaveChangesAsync();
        }

        JsonElement list = await admin.GetJsonAsync("/api/model-sync-events");

        // 只有进入选路的算进提示（Q140）。
        Assert.Equal(2, list.GetProperty("unacknowledgedEntered").GetInt32());
        JsonElement[] events = [.. list.GetProperty("events").EnumerateArray()];
        Assert.Equal(["gpt-5-mini", "gpt-4-old", "gpt-5-turbo"], events.Select(e => e.GetProperty("upstreamModel").GetString()));
        Assert.Equal("enteredRouting", events[0].GetProperty("kind").GetString());
        Assert.Equal("relay", events[0].GetProperty("channelName").GetString());

        (HttpStatusCode status, _) = await admin.PostAsync("/api/model-sync-events/acknowledge", new { });
        Assert.Equal(HttpStatusCode.NoContent, status);

        JsonElement after = await admin.GetJsonAsync("/api/model-sync-events");
        Assert.Equal(0, after.GetProperty("unacknowledgedEntered").GetInt32());
        Assert.All(after.GetProperty("events").EnumerateArray(), e => Assert.True(e.GetProperty("acknowledged").GetBoolean()));
    }

    [Fact]
    public async Task SyncRoutesNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/model-sync-events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/model-sync-events/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/channels/1/sync", null)).StatusCode);
    }

    private static ModelSyncEvent Event(ModelSyncEventKind kind, string model, DateTimeOffset at) => new()
    {
        OccurredAt = at,
        Kind = kind,
        ChannelId = 1,
        ChannelName = "relay",
        UpstreamModel = model,
        PublishedModels = "gpt-5",
    };
}
