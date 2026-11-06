using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Live;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 实时链路（Q110–Q113）、中断（Q115/Q116）与 Affinity（Q118–Q122）在中继里的行为。
/// </summary>
public sealed class LiveRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n";
    private const string Done = "data: [DONE]\n\n";

    private const string StreamBody = "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}";
    private const string PlainBody = "{\"model\":\"gpt-4o\",\"messages\":[]}";

    private static HttpResponseMessage Ok() => ScriptedUpstream.Sse(Content, Finish, Usage, Done);

    private static HttpResponseMessage Unauthorized() =>
        ScriptedUpstream.Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    /// <summary>一个 Conversation：同样的 system 和第一条用户消息，后面追加的轮次不影响身份。</summary>
    private static string Conversation(string topic, int turns = 0)
    {
        var messages = new List<object>
        {
            new { role = "system", content = "you are terse" },
            new { role = "user", content = topic },
        };
        for (int i = 0; i < turns; i++)
        {
            messages.Add(new { role = "assistant", content = "answer " + i });
            messages.Add(new { role = "user", content = "more " + i });
        }

        return JsonSerializer.Serialize(new { model = "gpt-4o", stream = true, messages });
    }

    private static InFlightRegistry Registry(RelayHost host) => host.Services.GetRequiredService<InFlightRegistry>();

    private static async Task<InFlightRequest> WaitForAsync(RelayHost host, Func<InFlightRequest, bool> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            InFlightRequest? match = Registry(host).Snapshot().FirstOrDefault(condition);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("进行中的请求没有到达预期状态。");
    }

    [Fact]
    public async Task ShutdownWaitsThenInterruptsWhatIsLeftWithARetryable503()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a"]));
        host.Upstream.ScriptSilent("sk-a");

        Task<HttpResponseMessage> pending = host.PostAsync(PlainBody);
        await WaitForAsync(host, r => r.Attempts.Count == 1);
        var drainer = new ShutdownDrainer(Registry(host), TimeProvider.System, NullLogger<ShutdownDrainer>.Instance)
        {
            GraceOverride = TimeSpan.FromMilliseconds(200),
            AfterInterruptOverride = TimeSpan.FromSeconds(5),
        };
        await drainer.DrainAsync(CancellationToken.None);
        Assert.Equal(0, Registry(host).Count);

        // 停机切掉的请求回 503，客户端可以重试；不是 Admin 中断的 403。
        using HttpResponseMessage response = await pending;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using (JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("gateway_shutting_down", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(503, record.HttpStatusCode);
        Assert.Equal("网关关闭时被中断", record.BlockDetail);
    }

    [Fact]
    public async Task InterruptingBeforeTheUpstreamAnswersGives403AndNoFailover()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.ScriptSilent("sk-a");
        host.Upstream.Script("sk-b", Ok);

        Task<HttpResponseMessage> pending = host.PostAsync(PlainBody);
        InFlightRequest running = await WaitForAsync(host, r => r.Attempts.Count == 1);
        Assert.True(Registry(host).TryInterrupt(running.RequestId));

        using HttpResponseMessage response = await pending;
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("request_interrupted", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // 中断不换候选，不冷却，不算健康度。
        Assert.Equal(["sk-a"], host.Upstream.SecretsTried);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
        int channelA = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels
            .Single(c => c.ChannelName == "a").ChannelId;
        Assert.Equal(1.0, host.Services.GetRequiredService<HealthTracker>().Factor(channelA, "gpt-4o"));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Aborted, record.Status);
        Assert.Equal(403, record.HttpStatusCode);
        Assert.False(record.ClientDisconnected);
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(FailureClass.Interrupted, attempt.FailureClass);
        Assert.Equal(CooldownScope.None, attempt.CooldownScope);
    }

    [Fact]
    public async Task InterruptingAStreamAfterCommitEndsItWithAnErrorFrameAndStillBillsTheOutput()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.ScriptHanging("sk-a", Content, Content);

        Task<HttpResponseMessage> pending = host.PostAsync(StreamBody);
        InFlightRequest running = await WaitForAsync(host, r => r.CommittedAt is not null);
        Assert.True(Registry(host).TryInterrupt(running.RequestId));

        using HttpResponseMessage response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"ok\"", text, StringComparison.Ordinal);
        Assert.Contains("request_interrupted", text, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Aborted, record.Status);
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(FailureClass.Interrupted, attempt.FailureClass);
        Assert.True(attempt.Committed);

        // 上游没给用量：和断开一样按已经看到的内容估算（Q20）。
        Assert.Equal(UsageSource.GatewayEstimated, attempt.UsageSource);
        Assert.True(attempt.OutputTokens > 0);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task OnlyRequestsStillInFlightCanBeInterrupted()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Guid id = Guid.Parse(response.Headers.GetValues("x-ntm-request-id").Single());
        for (int i = 0; i < 200 && Registry(host).Find(id) is not null; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(Registry(host).TryInterrupt(id));
        Assert.False(Registry(host).TryInterrupt(Guid.NewGuid()));
    }

    [Fact]
    public async Task EveryStateTransitionIsPublishedInOrder()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", Ok);
        using LiveSubscription subscription = host.Services.GetRequiredService<LiveHub>().TrySubscribe()!;

        using (HttpResponseMessage response = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var events = new List<LiveEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (events.LastOrDefault() is not RequestFinishedEvent)
        {
            events.Add(await subscription.Reader.ReadAsync(timeout.Token));
        }

        Assert.Collection(
            events,
            e => Assert.IsType<RequestStartedEvent>(e),
            e => Assert.Equal("a", Assert.IsType<AttemptStartedEvent>(e).Attempt.ChannelName),
            e => Assert.NotNull(Assert.IsType<CommittedEvent>(e).TimeToFirstContentMs),
            e => Assert.Equal(FailureClass.None, Assert.IsType<AttemptFinishedEvent>(e).Attempt.FailureClass),
            e => Assert.Equal(RequestStatus.Succeeded, Assert.IsType<RequestFinishedEvent>(e).Status));
        Assert.Empty(Registry(host).Snapshot());
    }

    [Fact]
    public async Task AConversationStaysOnItsChannelWhileOthersAreSpread()
    {
        // 两个渠道同档同权重：没有 Affinity 时平滑轮询会一人一次。
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);

        for (int turn = 0; turn < 3; turn++)
        {
            using HttpResponseMessage response = await host.PostAsync(Conversation("topic one", turn));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (HttpResponseMessage other = await host.PostAsync(Conversation("topic two")))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        List<string> tried = [.. host.Upstream.SecretsTried];
        Assert.Equal(tried[0], tried[1]);
        Assert.Equal(tried[0], tried[2]);
        Assert.NotEqual(tried[0], tried[3]);

        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(
            [AffinityOutcome.New, AffinityOutcome.Kept, AffinityOutcome.Kept, AffinityOutcome.New],
            records.Select(r => r.Affinity));
    }

    [Fact]
    public async Task WhenThePinnedChannelFailsTheConversationMovesAndStaysAtTheNewPlace()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);

        using (HttpResponseMessage first = await host.PostAsync(Conversation("topic")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        string pinned = host.Upstream.SecretsTried[0];
        string other = pinned == "sk-a" ? "sk-b" : "sk-a";
        host.Upstream.Script(pinned, Unauthorized);

        using (HttpResponseMessage second = await host.PostAsync(Conversation("topic", 1)))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using (HttpResponseMessage third = await host.PostAsync(Conversation("topic", 2)))
        {
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        }

        Assert.Equal([pinned, pinned, other, other], host.Upstream.SecretsTried);
        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(
            [AffinityOutcome.New, AffinityOutcome.Moved, AffinityOutcome.Kept],
            records.Select(r => r.Affinity));
    }

    [Fact]
    public async Task APinnedKeyThatFailsFallsBackToAnotherKeyOfTheSameChannelFirst()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a1", "sk-a2"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a1", Ok);
        host.Upstream.Script("sk-a2", Ok);
        host.Upstream.Script("sk-b", Ok);

        // 记住的是优先级较低的 sk-a2：它应排在 sk-a1 前面。
        ConfigSnapshot snapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;
        ChannelSnapshot channelA = snapshot.Channels.Single(c => c.ChannelName == "a");
        ChannelKeySnapshot second = channelA.Keys.Single(k => k.Secret == "sk-a2");
        ApiKeySnapshot apiKey = snapshot.FindApiKey(SecretFingerprint.Compute(RelayHost.ApiKeySecret))!;
        string body = Conversation("pinned key");
        UInt128 conversation = ConversationKey.Compute(
            ApiFormat.OpenAiChat, Encoding.UTF8.GetBytes(body), apiKey.ApiKeyId, "gpt-4o")!.Value;
        AffinityTable table = host.Services.GetRequiredService<AffinityTable>();
        table.Remember(conversation, new AffinityPin(channelA.ChannelId, "gpt-4o", second.Fingerprint));

        using (HttpResponseMessage kept = await host.PostAsync(body))
        {
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        }

        // 记住的密钥坏了：先换同渠道的 sk-a1，而不是换到渠道 b。
        host.Upstream.Script("sk-a2", Unauthorized);
        using (HttpResponseMessage fallback = await host.PostAsync(Conversation("pinned key", 1)))
        {
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        }

        Assert.Equal(["sk-a2", "sk-a2", "sk-a1"], host.Upstream.SecretsTried);
        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal([AffinityOutcome.Kept, AffinityOutcome.Moved], records.Select(r => r.Affinity));
    }

    [Fact]
    public async Task AConversationPinnedToABackupReturnsToTheHigherPriorityChannel()
    {
        await using RelayHost host = await RelayHost.StartAsync(("main", 10, ["sk-main"]), ("backup", 0, ["sk-backup"]));
        host.Upstream.Script("sk-main", Ok);
        host.Upstream.Script("sk-backup", Ok);

        // 主力出故障期间这个 Conversation 落到了备用渠道，并被记住了。
        ConfigSnapshot snapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;
        ChannelSnapshot backup = snapshot.Channels.Single(c => c.ChannelName == "backup");
        ApiKeySnapshot apiKey = snapshot.FindApiKey(SecretFingerprint.Compute(RelayHost.ApiKeySecret))!;
        string body = Conversation("priority");
        UInt128 conversation = ConversationKey.Compute(
            ApiFormat.OpenAiChat, Encoding.UTF8.GetBytes(body), apiKey.ApiKeyId, "gpt-4o")!.Value;
        host.Services.GetRequiredService<AffinityTable>().Remember(
            conversation, new AffinityPin(backup.ChannelId, "gpt-4o", backup.Keys.Single().Fingerprint));

        // 主力恢复了：Priority 优先于 Affinity（Q120）。
        using (HttpResponseMessage response = await host.PostAsync(body))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(["sk-main"], host.Upstream.SecretsTried);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(AffinityOutcome.Moved, record.Affinity);
    }

    [Fact]
    public async Task AFailedLandingIsNotRemembered()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", ServerError);

        using (HttpResponseMessage response = await host.PostAsync(Conversation("doomed")))
        {
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0, host.Services.GetRequiredService<AffinityTable>().Count);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(AffinityOutcome.New, record.Affinity);
    }
}
