using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Server.Tests;

/// <summary>选路可见性（Q259–Q264、Q266）。</summary>
public sealed class RoutingApiTests
{
    private static readonly string[] Models = ["gpt-4o"];

    [Fact]
    public async Task TheRoutingStateShowsSharesBlocksHealthAndPriorityOverrides()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int main = await admin.CreateChannelAsync("main", Models, priority: 10, weight: 3);
        int side = await admin.CreateChannelAsync("side", Models, priority: 10, weight: 1);
        int backup = await admin.CreateChannelAsync("backup", Models, priority: 0);
        await admin.AddKeyAsync(main, "sk-main-0123456789");
        await admin.AddKeyAsync(side, "sk-side-0123456789");
        await admin.AddKeyAsync(backup, "sk-backup-0123456789");
        int model = await admin.CreateModelAsync("gpt", [
            new { kind = "exact", pattern = "gpt-4o", excludedChannelIds = new[] { backup } },
            new { kind = "exact", pattern = "gpt-4o", channelId = backup, priority = 5 },
        ], enabled: true);

        JsonElement state = await admin.GetJsonAsync(AdminClient.Model(model) + "/routing");
        Assert.True(state.GetProperty("modelEnabled").GetBoolean());
        JsonElement[] tiers = [.. state.GetProperty("tiers").EnumerateArray()];
        Assert.Equal([10, 5], tiers.Select(t => t.GetProperty("priority").GetInt32()));
        Assert.True(tiers[0].GetProperty("active").GetBoolean());
        Assert.False(tiers[1].GetProperty("active").GetBoolean());

        JsonElement[] top = [.. tiers[0].GetProperty("candidates").EnumerateArray()];
        Assert.Equal(0.75, top.Single(c => c.GetProperty("channelId").GetInt32() == main).GetProperty("share").GetDouble(), 3);
        Assert.Equal(0.25, top.Single(c => c.GetProperty("channelId").GetInt32() == side).GetProperty("share").GetDouble(), 3);

        // 规则把 backup 从渠道自己的 0 抬到了 5（Q266）；备用档不算份额（Q260）。
        JsonElement spare = Assert.Single(tiers[1].GetProperty("candidates").EnumerateArray());
        Assert.Equal(0, spare.GetProperty("channelPriority").GetInt32());
        Assert.Equal(JsonValueKind.Null, spare.GetProperty("share").ValueKind);

        // main 冷却、side 有失败：份额全给 side，main 写明冷却到几点。
        CooldownRegistry cooldowns = host.Services.GetRequiredService<CooldownRegistry>();
        HealthTracker health = host.Services.GetRequiredService<HealthTracker>();
        ChannelSnapshot mainSnapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single(c => c.ChannelId == main);
        CooldownPermit permit = cooldowns.TryAcquire(main, "gpt-4o", mainSnapshot.Keys[0].Fingerprint, out _)!;
        cooldowns.Complete(permit, main, "gpt-4o", mainSnapshot.Keys[0].Fingerprint, FailureClass.UpstreamOverloaded, null);
        health.Record(side, "gpt-4o", FailureClass.UpstreamServerError);
        health.Record(side, "gpt-4o", FailureClass.None);

        state = await admin.GetJsonAsync(AdminClient.Model(model) + "/routing");
        top = [.. state.GetProperty("tiers")[0].GetProperty("candidates").EnumerateArray()];
        JsonElement cooling = top.Single(c => c.GetProperty("channelId").GetInt32() == main);
        Assert.Equal("cooldown", cooling.GetProperty("block").GetString());
        Assert.NotEqual(JsonValueKind.Null, cooling.GetProperty("recoversAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, cooling.GetProperty("share").ValueKind);
        JsonElement serving = top.Single(c => c.GetProperty("channelId").GetInt32() == side);
        Assert.Equal(1.0, serving.GetProperty("share").GetDouble(), 3);
        Assert.Equal((1, 1), (serving.GetProperty("successes").GetInt32(), serving.GetProperty("failures").GetInt32()));

        // 候选预览也带渠道自己的优先级。
        JsonElement detail = await admin.GetJsonAsync(AdminClient.Model(model));
        JsonElement previewSpare = detail.GetProperty("route").GetProperty("priorityGroups")[1].GetProperty("candidates")[0];
        Assert.Equal(0, previewSpare.GetProperty("channelPriority").GetInt32());

        (HttpStatusCode missing, _) = await admin.GetAsync(AdminClient.Model(9999) + "/routing");
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task CooldownsCanBeClearedAllOrOneAndHealthShowsUp()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("main", ["gpt-4o", "gpt-4o-mini"]);
        JsonElement key = await admin.AddKeyAsync(id, "sk-main-0123456789");
        int keyId = key.GetProperty("id").GetInt32();

        CooldownRegistry cooldowns = host.Services.GetRequiredService<CooldownRegistry>();
        ChannelSnapshot channel = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single(c => c.ChannelId == id);
        string fingerprint = channel.Keys[0].Fingerprint;
        foreach ((string model, FailureClass failure) in new[] { ("gpt-4o", FailureClass.UpstreamOverloaded), ("gpt-4o-mini", FailureClass.RateLimited) })
        {
            CooldownPermit permit = cooldowns.TryAcquire(id, model, fingerprint, out _)!;
            cooldowns.Complete(permit, id, model, fingerprint, failure, null);
        }

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        JsonElement[] listed = [.. detail.GetProperty("cooldowns").EnumerateArray()];
        Assert.Equal(2, listed.Length);
        Assert.Contains(listed, c => c.GetProperty("keyId").ValueKind == JsonValueKind.Number && c.GetProperty("keyId").GetInt32() == keyId);

        using (HttpResponseMessage bad = await admin.Client.DeleteAsync(AdminClient.Channel(id) + "/cooldowns?scope=nope"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using (HttpResponseMessage unknownKey = await admin.Client.DeleteAsync(AdminClient.Channel(id) + "/cooldowns?keyId=9999"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownKey.StatusCode);
        }

        // 只解除密钥 × 模型那一条，渠道 × 模型的还在。
        using (HttpResponseMessage one = await admin.Client.DeleteAsync(
            AdminClient.Channel(id) + $"/cooldowns?scope=keyModel&keyId={keyId}&upstreamModel=gpt-4o-mini"))
        {
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            Assert.Equal(1, (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        }

        Assert.Single((await admin.GetJsonAsync(AdminClient.Channel(id))).GetProperty("cooldowns").EnumerateArray());

        using (HttpResponseMessage all = await admin.Client.DeleteAsync(AdminClient.Channel(id) + "/cooldowns"))
        {
            Assert.Equal(1, (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        }

        Assert.Empty((await admin.GetJsonAsync(AdminClient.Channel(id))).GetProperty("cooldowns").EnumerateArray());
        Assert.Null(cooldowns.CheckCandidate(id, "gpt-4o"));

        // 健康度：运行状态按上游模型列出，列表给出最低系数（Q261）。
        HealthTracker health = host.Services.GetRequiredService<HealthTracker>();
        for (int i = 0; i < 20; i++)
        {
            health.Record(id, "gpt-4o", i < 10 ? FailureClass.None : FailureClass.UpstreamServerError);
        }

        JsonElement runtime = await admin.GetJsonAsync(AdminClient.Channel(id) + "/runtime");
        JsonElement sample = Assert.Single(runtime.GetProperty("health").EnumerateArray());
        Assert.Equal("gpt-4o", sample.GetProperty("upstreamModel").GetString());
        Assert.Equal((10, 10), (sample.GetProperty("successes").GetInt32(), sample.GetProperty("failures").GetInt32()));
        Assert.Equal(0.5, sample.GetProperty("factor").GetDouble(), 3);

        JsonElement row = (await admin.GetJsonAsync("/api/channels")).EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == id);
        Assert.Equal(0.5, row.GetProperty("lowestHealth").GetDouble(), 3);
    }

    [Fact]
    public async Task RequestsFilterByChannelAndShowSkippedCandidates()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        Guid onTwo;
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            RequestRecord first = Record(DateTimeOffset.UtcNow.AddMinutes(-2), channelIds: [1]);
            RequestRecord second = Record(DateTimeOffset.UtcNow.AddMinutes(-1), channelIds: [1, 2]);
            second.Skip(3, "channel-3", "upstream", BlockReason.Cooldown, DateTimeOffset.UnixEpoch.AddDays(1));
            traffic.Requests.AddRange(first, second);
            await traffic.SaveChangesAsync();
            onTwo = second.Id;
        }

        JsonElement page = await admin.GetJsonAsync("/api/requests?channelId=2");
        Assert.Equal([onTwo.ToString()], page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(2, (await admin.GetJsonAsync("/api/requests?channelId=1")).GetProperty("items").GetArrayLength());

        JsonElement detail = await admin.GetJsonAsync("/api/requests/" + onTwo);
        JsonElement skipped = Assert.Single(detail.GetProperty("skipped").EnumerateArray());
        Assert.Equal("channel-3", skipped.GetProperty("channelName").GetString());
        Assert.Equal("cooldown", skipped.GetProperty("reason").GetString());
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), skipped.GetProperty("recoversAt").GetDateTimeOffset());
    }

    private static RequestRecord Record(DateTimeOffset startedAt, int[] channelIds)
    {
        var record = new RequestRecord
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(1),
            PublishedModel = "gpt",
            InboundApiFormat = ApiFormat.OpenAiChat,
            RequestKind = RequestKind.Chat,
            Status = RequestStatus.Succeeded,
            HttpStatusCode = 200,
            AttemptCount = channelIds.Length,
        };

        for (int i = 0; i < channelIds.Length; i++)
        {
            record.Attempts.Add(new AttemptRecord
            {
                Ordinal = i + 1,
                StartedAt = startedAt.AddMilliseconds(i * 100),
                FinishedAt = startedAt.AddMilliseconds((i * 100) + 50),
                ChannelId = channelIds[i],
                ChannelName = "channel-" + channelIds[i],
                ChannelKeyId = channelIds[i],
                ChannelKeyFingerprint = new string('0', 64),
                ChannelKeyPrefix = "sk-",
                UpstreamApiFormat = ApiFormat.OpenAiChat,
                UpstreamModel = "upstream",
                RequestUri = "https://example/v1/chat/completions",
                HttpStatusCode = 200,
                MultiplierBasisPoints = 10_000,
            });
        }

        return record;
    }
}
