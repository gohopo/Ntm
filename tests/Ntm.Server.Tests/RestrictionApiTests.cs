using System.Net;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>
/// Restriction 的配置接口（Q104）：整体替换，金额按美元填、存成纳美元，空值表示不限。
/// 运行状态是只读的另一个接口。
/// </summary>
public sealed class RestrictionApiTests
{
    private static readonly string[] MessyModels = [" gpt-4o ", "gpt-4o", "", "o3"];
    private static readonly string[] OneModel = ["gpt-4o"];
    private static readonly string[] Fast = ["fast"];

    private static object Window(int[] weekdays, string start, string end) => new { weekdays, start, end };

    [Fact]
    public async Task AChannelsLimitsRoundTripThroughTheDetailView()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/restrictions",
            new
            {
                dailyCostLimitUsd = 12.5m,
                timeWindows = new[] { Window([1, 2, 3, 4, 5], "09:00", "18:30") },
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(12_500_000_000, saved.GetProperty("dailyCostLimitNanoUsd").GetInt64());

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(channelId));
        JsonElement restrictions = detail.GetProperty("restrictions");
        Assert.Equal(12_500_000_000, restrictions.GetProperty("dailyCostLimitNanoUsd").GetInt64());

        JsonElement window = restrictions.GetProperty("timeWindows").EnumerateArray().Single();
        Assert.Equal([1, 2, 3, 4, 5], window.GetProperty("weekdays").EnumerateArray().Select(d => d.GetInt32()));
        Assert.Equal("09:00", window.GetProperty("start").GetString());
        Assert.Equal("18:30", window.GetProperty("end").GetString());

        // 清空就是不限。
        (_, JsonElement cleared) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/restrictions",
            new { dailyCostLimitUsd = (decimal?)null, timeWindows = Array.Empty<object>() });
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("dailyCostLimitNanoUsd").ValueKind);
        Assert.Empty(cleared.GetProperty("timeWindows").EnumerateArray());
    }

    [Fact]
    public async Task BadLimitsAndWindowsAreRejectedFieldByField()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);

        (HttpStatusCode status, JsonElement body) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/restrictions",
            new
            {
                dailyCostLimitUsd = 0.0000001m,
                timeWindows = new[] { Window([], "9:00", "25:00") },
            });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        string raw = body.GetRawText();
        Assert.Contains("dailyCostLimitUsd", raw, StringComparison.Ordinal);
        Assert.Contains("timeWindows[0].weekdays", raw, StringComparison.Ordinal);
        Assert.Contains("timeWindows[0].start", raw, StringComparison.Ordinal);
        Assert.Contains("timeWindows[0].end", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeysLimitsAreTrimmedAndDeduped()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);
        int keyId = (await admin.AddKeyAsync(channelId, "sk-aaaaaaaaaaaa")).GetProperty("id").GetInt32();

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/keys/" + keyId + "/restrictions",
            new
            {
                requestsPerMinute = 60,
                maxConcurrency = 4,
                allowedModels = MessyModels,
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(60, saved.GetProperty("requestsPerMinute").GetInt32());
        Assert.Equal(4, saved.GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(["gpt-4o", "o3"], saved.GetProperty("allowedModels").EnumerateArray().Select(m => m.GetString()));

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(channelId));
        JsonElement key = detail.GetProperty("keys").EnumerateArray().Single();
        Assert.Equal(60, key.GetProperty("restrictions").GetProperty("requestsPerMinute").GetInt32());

        (HttpStatusCode badStatus, JsonElement badBody) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/keys/" + keyId + "/restrictions",
            new { requestsPerMinute = 0, maxConcurrency = (int?)null, allowedModels = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Contains("requestsPerMinute", badBody.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiKeysChannelListsMustReferToRealChannels()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        int apiKeyId = created.GetProperty("view").GetProperty("id").GetInt32();

        (HttpStatusCode missing, JsonElement missingBody) = await admin.PutAsync(
            AdminClient.ApiKey(apiKeyId) + "/restrictions",
            new { allowedChannelIds = new[] { channelId + 999 } });
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Contains("allowedChannelIds", missingBody.GetRawText(), StringComparison.Ordinal);

        (HttpStatusCode both, JsonElement bothBody) = await admin.PutAsync(
            AdminClient.ApiKey(apiKeyId) + "/restrictions",
            new { allowedChannelIds = new[] { channelId }, blockedChannelIds = new[] { channelId } });
        Assert.Equal(HttpStatusCode.BadRequest, both);
        Assert.Contains("blockedChannelIds", bothBody.GetRawText(), StringComparison.Ordinal);

        (HttpStatusCode ok, JsonElement saved) = await admin.PutAsync(
            AdminClient.ApiKey(apiKeyId) + "/restrictions",
            new
            {
                costLimitUsd = 1.5m,
                maxConcurrency = 2,
                allowedModels = OneModel,
                allowedChannelIds = new[] { channelId },
            });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(1_500_000_000, saved.GetProperty("costLimitNanoUsd").GetInt64());

        JsonElement detail = await admin.GetJsonAsync(AdminClient.ApiKey(apiKeyId));
        Assert.Equal("ci", detail.GetProperty("apiKey").GetProperty("name").GetString());
        Assert.Equal(2, detail.GetProperty("restrictions").GetProperty("maxConcurrency").GetInt32());
        Assert.Equal([channelId], detail.GetProperty("restrictions").GetProperty("allowedChannelIds").EnumerateArray().Select(c => c.GetInt32()));
    }

    [Fact]
    public async Task ADeletedChannelStaysInTheListUntilItIsEditedAgain()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int keep = await admin.CreateChannelAsync("keep", ["gpt-4o"]);
        int drop = await admin.CreateChannelAsync("drop", ["gpt-4o"]);
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        int apiKeyId = created.GetProperty("view").GetProperty("id").GetInt32();

        await admin.PutAsync(
            AdminClient.ApiKey(apiKeyId) + "/restrictions",
            new { allowedChannelIds = new[] { keep, drop } });
        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync(AdminClient.Channel(drop)));

        // 删渠道不清名单：失效的 id 无害，清掉反而可能把「只允许这一个」变成「不限」。
        JsonElement detail = await admin.GetJsonAsync(AdminClient.ApiKey(apiKeyId));
        Assert.Equal([keep, drop], detail.GetProperty("restrictions").GetProperty("allowedChannelIds").EnumerateArray().Select(c => c.GetInt32()));

        // 存着的失效 id 照收，不然这个密钥就改不动了。
        (HttpStatusCode status, _) = await admin.PutAsync(
            AdminClient.ApiKey(apiKeyId) + "/restrictions",
            new { allowedChannelIds = new[] { keep, drop } });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task RenamingAPublishedModelFollowsTheApiKeysThatAllowIt()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateChannelAsync("a", ["gpt-4o"]);
        int modelId = await admin.CreateModelAsync("fast", [new { kind = "Exact", pattern = "gpt-4o" }]);
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        int apiKeyId = created.GetProperty("view").GetProperty("id").GetInt32();
        await admin.PutAsync(AdminClient.ApiKey(apiKeyId) + "/restrictions", new { allowedModels = Fast });

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsync(AdminClient.Model(modelId), new { name = "quick", enabled = false, rules = new[] { new { kind = "Exact", pattern = "gpt-4o" } } })).Status);

        JsonElement detail = await admin.GetJsonAsync(AdminClient.ApiKey(apiKeyId));
        Assert.Equal(["quick"], detail.GetProperty("restrictions").GetProperty("allowedModels").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task TheRuntimeViewsReportSpendAndLiveCounters()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);
        await admin.AddKeyAsync(channelId, "sk-aaaaaaaaaaaa");
        await admin.PutAsync(AdminClient.Channel(channelId) + "/restrictions", new { dailyCostLimitUsd = 1m });
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        int apiKeyId = created.GetProperty("view").GetProperty("id").GetInt32();

        JsonElement channel = await admin.GetJsonAsync(AdminClient.Channel(channelId) + "/runtime");
        Assert.Equal(1_000_000_000, channel.GetProperty("dailyCostLimitNanoUsd").GetInt64());
        Assert.Equal(0, channel.GetProperty("spentTodayNanoUsd").GetInt64());
        Assert.Equal(0, channel.GetProperty("unpricedTodayCount").GetInt64());
        Assert.Equal(0, channel.GetProperty("inFlight").GetInt32());
        Assert.True(channel.GetProperty("inTimeWindow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("nextWindowStart").ValueKind);
        Assert.NotEmpty(channel.GetProperty("timeZone").GetString()!);

        JsonElement key = channel.GetProperty("keys").EnumerateArray().Single();
        Assert.Equal(0, key.GetProperty("requestsThisMinute").GetInt32());
        Assert.Equal(0, key.GetProperty("inFlight").GetInt32());

        JsonElement apiKey = await admin.GetJsonAsync(AdminClient.ApiKey(apiKeyId) + "/runtime");
        Assert.Equal(0, apiKey.GetProperty("spentNanoUsd").GetInt64());
        Assert.Equal(JsonValueKind.Null, apiKey.GetProperty("costLimitNanoUsd").ValueKind);
        Assert.Equal(0, apiKey.GetProperty("inFlight").GetInt32());
    }

    [Fact]
    public async Task AChannelOutsideItsWindowReportsWhenItOpensAgain()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("a", ["gpt-4o"]);

        // 选上除今天以外的每一天：不管现在几点，今天都关着。
        JsonElement zone = await admin.GetJsonAsync("/api/settings/time-zone");
        DateTime localNow = DateTime.ParseExact(zone.GetProperty("localNow").GetString()!, "yyyy-MM-dd HH:mm", null);
        int[] weekdays = [.. Enumerable.Range(0, 7).Where(d => d != (int)localNow.DayOfWeek)];

        await admin.PutAsync(
            AdminClient.Channel(channelId) + "/restrictions",
            new { timeWindows = new[] { Window(weekdays, "00:00", "00:00") } });

        JsonElement runtime = await admin.GetJsonAsync(AdminClient.Channel(channelId) + "/runtime");
        Assert.False(runtime.GetProperty("inTimeWindow").GetBoolean());
        Assert.True(runtime.GetProperty("nextWindowStart").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TheTimeZoneEndpointNamesTheZoneTheLimitsUse()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        JsonElement zone = await admin.GetJsonAsync("/api/settings/time-zone");

        Assert.Equal(TimeZoneInfo.Local.Id, zone.GetProperty("id").GetString());
        Assert.False(zone.GetProperty("isFallback").GetBoolean());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", zone.GetProperty("localNow").GetString()!);
    }

    [Fact]
    public async Task RestrictionRoutesNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage runtime = await anonymous.GetAsync("/api/channels/1/runtime");
        using HttpResponseMessage zone = await anonymous.GetAsync("/api/settings/time-zone");

        Assert.Equal(HttpStatusCode.Unauthorized, runtime.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, zone.StatusCode);
    }
}
