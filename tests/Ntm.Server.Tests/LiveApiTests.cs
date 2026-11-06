using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Live;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Server.Tests;

/// <summary>实时链路（Q110/Q114）、中断接口（Q115）和进行中请求的详情（Q112）。</summary>
public sealed class LiveApiTests
{
    private static RequestRecord Running(string model = "gpt-4o") => new()
    {
        Id = Guid.CreateVersion7(),
        StartedAt = DateTimeOffset.UtcNow,
        ApiKeyId = 1,
        ApiKeyName = "default",
        PublishedModel = model,
        InboundApiFormat = ApiFormat.OpenAiChat,
        RequestKind = RequestKind.Chat,
        IsStream = true,
        Status = RequestStatus.Running,
    };

    private static async Task<JsonElement> ReadEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            string? line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(line["data: ".Length..]);
                return document.RootElement.Clone();
            }
        }
    }

    [Fact]
    public async Task TheStreamStartsWithASnapshotAndThenSendsChanges()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        InFlightRegistry registry = host.Services.GetRequiredService<InFlightRegistry>();
        RequestRecord before = Running();
        registry.Register(before);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/live");
        using HttpResponseMessage response = await admin.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        JsonElement snapshot = await ReadEventAsync(reader);
        Assert.Equal("snapshot", snapshot.GetProperty("type").GetString());
        JsonElement listed = Assert.Single(snapshot.GetProperty("requests").EnumerateArray().ToList());
        Assert.Equal(before.Id, listed.GetProperty("requestId").GetGuid());

        RequestRecord after = Running("o3");
        registry.Register(after);
        JsonElement started = await ReadEventAsync(reader);
        Assert.Equal("requestStarted", started.GetProperty("type").GetString());
        Assert.Equal("o3", started.GetProperty("request").GetProperty("publishedModel").GetString());

        after.Status = RequestStatus.Succeeded;
        registry.Complete(after);
        JsonElement finished = await ReadEventAsync(reader);
        Assert.Equal("requestFinished", finished.GetProperty("type").GetString());
        Assert.Equal("succeeded", finished.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARunningRequestCanBeReadAndInterrupted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        InFlightRegistry registry = host.Services.GetRequiredService<InFlightRegistry>();
        RequestRecord record = Running();
        InFlightRequest running = registry.Register(record);
        string url = "/api/requests/" + record.Id;

        (HttpStatusCode status, JsonElement detail) = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("running", detail.GetProperty("status").GetString());
        Assert.False(detail.GetProperty("interruptRequested").GetBoolean());

        (HttpStatusCode accepted, _) = await admin.PostAsync(url + "/interrupt", new { });
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        Assert.True(running.InterruptRequested);
        Assert.True((await admin.GetJsonAsync(url)).GetProperty("interruptRequested").GetBoolean());

        registry.Complete(record);
        (HttpStatusCode gone, _) = await admin.PostAsync(url + "/interrupt", new { });
        Assert.Equal(HttpStatusCode.NotFound, gone);
    }

    [Fact]
    public async Task TheLiveStreamAndInterruptNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage live = await anonymous.GetAsync("/api/live");
        Assert.Equal(HttpStatusCode.Unauthorized, live.StatusCode);

        using HttpResponseMessage interrupt = await anonymous.PostAsync("/api/requests/" + Guid.NewGuid() + "/interrupt", null);
        Assert.Equal(HttpStatusCode.Unauthorized, interrupt.StatusCode);
    }
}
