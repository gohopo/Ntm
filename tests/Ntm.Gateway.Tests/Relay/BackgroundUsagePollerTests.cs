using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// background 响应的用量由网关主动去拉（Q305）：到期才拉、到了终态回填一次、
/// 404 或满 24 小时放弃、重启后从库里接着拉。
/// </summary>
public sealed class BackgroundUsagePollerTests
{
    private const string Queued = """{"id":"resp_bg","object":"response","status":"queued","background":true,"output":[]}""";
    private const string Running = """{"id":"resp_bg","object":"response","status":"in_progress","background":true,"output":[]}""";
    private const string Done = """{"id":"resp_bg","object":"response","status":"completed","output":[],"usage":{"input_tokens":40,"output_tokens":7}}""";

    /// <summary>真实时钟加一个可调的偏移：刚落下的落点在它看来已经过了一会儿。</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    private static BackgroundUsagePoller PollerFor(RelayHost host, TimeProvider time, ResponseLandingTable? landings = null)
    {
        IServiceProvider services = host.Services;
        landings ??= services.GetRequiredService<ResponseLandingTable>();
        var backfill = new ResponseUsageBackfill(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ConfigSnapshotProvider>(),
            services.GetRequiredService<CostLedger>(),
            landings,
            NullLogger<ResponseUsageBackfill>.Instance);
        return new BackgroundUsagePoller(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ConfigSnapshotProvider>(),
            services.GetRequiredService<UpstreamForwarder>(),
            landings,
            backfill,
            time,
            NullLogger<BackgroundUsagePoller>.Instance);
    }

    /// <summary>发一个 background 请求，等它的请求记录和落点都进了库。</summary>
    private static async Task<RelayHost> StartWithQueuedAsync(Func<int, HttpResponseMessage> poll)
    {
        RelayHost host = await RelayHost.StartWithFormatsAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        int polls = 0;
        host.Upstream.Script("sk-r", (method, _, _) => method == HttpMethod.Post
            ? ScriptedUpstream.Json(HttpStatusCode.OK, Queued)
            : poll(Interlocked.Increment(ref polls)));

        using HttpResponseMessage queued = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi","background":true}""");
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Guid requestId = Guid.Parse(queued.Headers.GetValues("x-ntm-request-id").Single());

        await host.Services.GetRequiredService<ResponseLandingWriter>().FlushAsync();
        for (int i = 0; i < 50; i++)
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<TrafficDbContext>().Requests.AnyAsync(r => r.Id == requestId))
            {
                break;
            }

            await Task.Delay(100);
        }

        return host;
    }

    private static int Polls(RelayHost host) => host.Upstream.Methods.Count(m => m == HttpMethod.Get);

    [Fact]
    public async Task PollsWhenDueAndBackfillsOnceTheResponseFinishes()
    {
        await using RelayHost host = await StartWithQueuedAsync(n => ScriptedUpstream.Json(HttpStatusCode.OK, n == 1 ? Running : Done));
        var time = new ShiftedTime();
        BackgroundUsagePoller poller = PollerFor(host, time);

        // 不到 30 秒不拉。
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));

        time.Offset = TimeSpan.FromMinutes(1);
        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));

        // 还在跑：一分钟后再拉，没到就不拉。
        time.Offset = TimeSpan.FromMinutes(1.5);
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));

        time.Offset = TimeSpan.FromMinutes(2.1);
        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));

        // 回填过了，不再拉。
        time.Offset = TimeSpan.FromMinutes(5);
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(2, Polls(host));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(40, record.InputTokens);
        Assert.Equal(7, record.OutputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);
        Assert.All(
            host.Upstream.Calls.Where(c => c.Body.Length == 0),
            call => Assert.EndsWith("/v1/responses/resp_bg", call.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowsDownToEveryHalfHourAfterTheFirstHour()
    {
        // 一直排队：满 10 分钟后每 10 分钟，满 1 小时后每 30 分钟（Q317）。
        await using RelayHost host = await StartWithQueuedAsync(_ => ScriptedUpstream.Json(HttpStatusCode.OK, Running));
        var time = new ShiftedTime { Offset = TimeSpan.FromMinutes(50) };
        BackgroundUsagePoller poller = PollerFor(host, time);

        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));

        time.Offset = TimeSpan.FromMinutes(61);
        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));

        time.Offset = TimeSpan.FromMinutes(80);
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));

        time.Offset = TimeSpan.FromMinutes(92);
        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(3, Polls(host));
    }

    [Fact]
    public async Task GivesUpOnUpstream404()
    {
        await using RelayHost host = await StartWithQueuedAsync(_ => ScriptedUpstream.Json(HttpStatusCode.NotFound, """{"error":{"message":"No response found"}}"""));
        var time = new ShiftedTime { Offset = TimeSpan.FromMinutes(1) };
        BackgroundUsagePoller poller = PollerFor(host, time);

        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));
        await host.Services.GetRequiredService<ResponseLandingWriter>().FlushAsync();

        time.Offset = TimeSpan.FromHours(1);
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(1, Polls(host));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(UsageSource.Unavailable, record.UsageSource);
    }

    [Fact]
    public async Task GivesUpAfterADayWithoutAskingUpstream()
    {
        await using RelayHost host = await StartWithQueuedAsync(_ => ScriptedUpstream.Json(HttpStatusCode.OK, Running));
        var time = new ShiftedTime { Offset = TimeSpan.FromHours(25) };
        BackgroundUsagePoller poller = PollerFor(host, time);

        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));
        await host.Services.GetRequiredService<ResponseLandingWriter>().FlushAsync();
        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, Polls(host));

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        ResponseLanding stored = await scope.ServiceProvider.GetRequiredService<TrafficDbContext>()
            .ResponseLandings.AsNoTracking().SingleAsync(l => l.Id == "resp_bg");
        Assert.False(stored.AwaitingUsage);
    }

    [Fact]
    public async Task ResumesFromTheDatabaseAfterARestart()
    {
        await using RelayHost host = await StartWithQueuedAsync(_ => ScriptedUpstream.Json(HttpStatusCode.OK, Done));
        var time = new ShiftedTime { Offset = TimeSpan.FromMinutes(1) };

        // 新的落点表什么都没缓存，就像重启过一样。
        var fresh = new ResponseLandingTable(
            time,
            host.Services.GetRequiredService<IServiceScopeFactory>());
        BackgroundUsagePoller poller = PollerFor(host, time, fresh);

        Assert.Equal(1, await poller.PollOnceAsync(CancellationToken.None));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(40, record.InputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);
    }
}
