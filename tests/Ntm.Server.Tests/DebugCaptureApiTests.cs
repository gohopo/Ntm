using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Debugging;

namespace Ntm.Server.Tests;

/// <summary>DebugCapture 的开关与原文接口（Q154–Q162）。</summary>
public sealed class DebugCaptureApiTests
{
    [Fact]
    public async Task TheGlobalSwitchTurnsOnForTheDefaultHourAndOffWithZero()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        JsonElement initial = await admin.GetJsonAsync("/api/settings/debug-capture");
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("until").ValueKind);
        Assert.Equal(60, initial.GetProperty("defaultDurationMinutes").GetInt32());
        Assert.Equal(24 * 60, initial.GetProperty("maxDurationMinutes").GetInt32());
        Assert.Equal(24, initial.GetProperty("retentionHours").GetInt32());
        Assert.Equal(0, initial.GetProperty("dropped").GetInt64());

        (HttpStatusCode on, JsonElement opened) = await admin.PutAsync("/api/settings/debug-capture", new { minutes = (int?)null });
        Assert.Equal(HttpStatusCode.OK, on);
        DateTimeOffset until = opened.GetProperty("until").GetDateTimeOffset();
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        // 开关进了快照，转发看得到。
        ConfigSnapshot snapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;
        Assert.Equal(until, snapshot.DebugCaptureUntil);

        (HttpStatusCode off, JsonElement closed) = await admin.PutAsync("/api/settings/debug-capture", new { minutes = 0 });
        Assert.Equal(HttpStatusCode.OK, off);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("until").ValueKind);
    }

    [Fact]
    public async Task ADurationOutsideTheLimitIsRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode tooLong, JsonElement body) = await admin.PutAsync("/api/settings/debug-capture", new { minutes = 24 * 60 + 1 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong);
        Assert.True(body.GetProperty("errors").TryGetProperty("minutes", out _));

        (HttpStatusCode negative, _) = await admin.PutAsync("/api/settings/debug-capture", new { minutes = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, negative);
    }

    [Fact]
    public async Task ChannelAndApiKeySwitchesHaveTheirOwnEndpoints()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channelId = await admin.CreateChannelAsync("alpha");
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        int apiKeyId = created.GetProperty("view").GetProperty("id").GetInt32();

        (HttpStatusCode channelOn, JsonElement channelView) = await admin.PutAsync(
            AdminClient.Channel(channelId) + "/debug-capture", new { minutes = 15 });
        Assert.Equal(HttpStatusCode.OK, channelOn);
        DateTimeOffset channelUntil = channelView.GetProperty("until").GetDateTimeOffset();
        Assert.InRange(channelUntil - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));

        JsonElement channelRead = await admin.GetJsonAsync(AdminClient.Channel(channelId) + "/debug-capture");
        Assert.Equal(channelUntil, channelRead.GetProperty("until").GetDateTimeOffset());

        (HttpStatusCode keyOn, _) = await admin.PutAsync(AdminClient.ApiKey(apiKeyId) + "/debug-capture", new { minutes = 30 });
        Assert.Equal(HttpStatusCode.OK, keyOn);
        JsonElement keyRead = await admin.GetJsonAsync(AdminClient.ApiKey(apiKeyId) + "/debug-capture");
        Assert.Equal(JsonValueKind.String, keyRead.GetProperty("until").ValueKind);

        ConfigSnapshot snapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;
        Assert.Equal(channelUntil, snapshot.Channels.Single(c => c.ChannelId == channelId).DebugCaptureUntil);

        (HttpStatusCode missing, _) = await admin.PutAsync(AdminClient.Channel(999) + "/debug-capture", new { minutes = 5 });
        Assert.Equal(HttpStatusCode.NotFound, missing);
        (HttpStatusCode missingKey, _) = await admin.GetAsync(AdminClient.ApiKey(999) + "/debug-capture");
        Assert.Equal(HttpStatusCode.NotFound, missingKey);
    }

    [Fact]
    public async Task ARequestsPayloadsAreListedInOrderAndCanBeDownloaded()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        var requestId = Guid.CreateVersion7();

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            DebugDbContext db = scope.ServiceProvider.GetRequiredService<DebugDbContext>();
            foreach (int attempt in new[] { 2, 1 })
            {
                foreach (DebugPayloadKind kind in new[] { DebugPayloadKind.UpstreamResponse, DebugPayloadKind.ClientRequest })
                {
                    db.DebugPayloads.Add(new DebugPayload
                    {
                        RequestId = requestId,
                        AttemptOrdinal = attempt,
                        Kind = kind,
                        CapturedAt = DateTimeOffset.UtcNow,
                        HttpStatusCode = kind == DebugPayloadKind.UpstreamResponse ? 200 : null,
                        Headers = "Accept: application/json\n",
                        Body = "{\"n\":" + attempt + "}",
                        Truncated = attempt == 2,
                        ByteCount = 7,
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        JsonElement list = await admin.GetJsonAsync($"/api/requests/{requestId}/debug");
        Assert.Equal(4, list.GetArrayLength());
        Assert.Equal(
            ["1:clientRequest", "1:upstreamResponse", "2:clientRequest", "2:upstreamResponse"],
            list.EnumerateArray().Select(p => p.GetProperty("attemptOrdinal").GetInt32() + ":" + p.GetProperty("kind").GetString()));
        Assert.True(list[3].GetProperty("truncated").GetBoolean());
        Assert.Equal(200, list[1].GetProperty("httpStatusCode").GetInt32());

        using HttpResponseMessage download = await admin.Client.GetAsync($"/api/requests/{requestId}/debug?download=true");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(requestId.ToString("N"), download.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);

        JsonElement none = await admin.GetJsonAsync($"/api/requests/{Guid.CreateVersion7()}/debug");
        Assert.Equal(0, none.GetArrayLength());
    }

    [Fact]
    public async Task DebugEndpointsNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateClient();

        using HttpResponseMessage settings = await anonymous.GetAsync("/api/settings/debug-capture");
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);

        using HttpResponseMessage payloads = await anonymous.GetAsync($"/api/requests/{Guid.CreateVersion7()}/debug");
        Assert.Equal(HttpStatusCode.Unauthorized, payloads.StatusCode);
    }
}
