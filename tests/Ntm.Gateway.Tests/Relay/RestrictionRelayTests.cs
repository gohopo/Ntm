using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Live;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 切片 6 的 Restriction：渠道上的每日费用与可用时段、渠道密钥上的 RPM / 并发 / 模型白名单让候选退出选路，
/// 不进入 Cooldown；API 密钥上的费用上限、模型限制与并发直接拒绝 Request（Q77–Q93）。
/// </summary>
public sealed class RestrictionRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private const string StreamBody = "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}";

    private static HttpResponseMessage Ok() => ScriptedUpstream.Sse(Content, Finish, Usage, Done);

    /// <summary>和默认种子一样下发模型，另外给 gpt-4o 配上单价（1000×2.5 + 200×10 → 4_500_000 纳美元），再改一点配置。</summary>
    private static Action<ConfigDbContext, IReadOnlyList<Channel>> Configure(
        Action<ConfigDbContext, IReadOnlyList<Channel>, ApiKey>? change = null) =>
        (db, channels) =>
        {
            foreach (Channel channel in channels)
            {
                channel.Provider = "openai";
                foreach (string model in RelayHost.Models)
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                }
            }

            foreach (string model in RelayHost.Models)
            {
                var published = new PublishedModel { Name = model, Enabled = true };
                published.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = model });
                db.PublishedModels.Add(published);
            }

            db.PriceRows.Add(new PriceRow
            {
                Provider = "openai",
                UpstreamModel = "gpt-4o",
                Source = PriceSource.Manual,
                InputPrice = 2.5m,
                OutputPrice = 10m,
            });

            change?.Invoke(db, channels, db.ApiKeys.Local.Single());
        };

    private static RestrictionChecker Checker(RelayHost host) => host.Services.GetRequiredService<RestrictionChecker>();

    private static ConfigSnapshot Snapshot(RelayHost host) => host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;

    /// <summary>花费是在响应写完后的 finally 里记的，客户端读完响应时可能还没记上。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task AnApiKeyThatHasSpentItsLimitIsRejectedWithInsufficientQuota()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, _, key) => key.CostLimitNanoUsd = 4_000_000));
        host.Upstream.Script("sk-a", Ok);
        int apiKeyId = Snapshot(host).FindApiKey(SecretFingerprint.Compute(RelayHost.ApiKeySecret))!.ApiKeyId;

        // 软上限（Q107）：发出前还没到上限就放行，这一次花掉 4_500_000，超了一点。
        using (HttpResponseMessage first = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await WaitUntilAsync(() => Checker(host).Costs.ApiKeyTotal(apiKeyId) == 4_500_000);

        using HttpResponseMessage second = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("insufficient_quota", await ErrorCodeAsync(second));
        Assert.Null(second.Headers.RetryAfter);

        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(2, records.Count);
        Assert.Empty(records[1].Attempts);
        Assert.Equal(RequestStatus.Failed, records[1].Status);
        Assert.Equal(FailureClass.QuotaBlocked, records[1].BlockedBy);
        Assert.Single(host.Upstream.Calls);
    }

    [Fact]
    public async Task AModelOutsideTheApiKeysListIsForbidden()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, _, key) => key.AllowedModels.Add("claude-x")));
        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage denied = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("model_not_allowed", await ErrorCodeAsync(denied));

        using HttpResponseMessage allowed = await host.PostAsync("{\"model\":\"claude-x\",\"stream\":true,\"messages\":[]}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(FailureClass.RestrictionBlocked, records[0].BlockedBy);
        Assert.Empty(records[0].Attempts);
        Assert.Null(records[1].BlockedBy);
    }

    [Fact]
    public async Task AnApiKeyAtItsConcurrencyLimitGets429UntilALeaseIsReturned()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, _, key) => key.MaxConcurrency = 1));
        host.Upstream.Script("sk-a", Ok);
        ApiKeySnapshot token = Snapshot(host).FindApiKey(SecretFingerprint.Compute(RelayHost.ApiKeySecret))!;

        // 占住唯一的位置，相当于另一个请求正在进行。
        IDisposable lease = Checker(host).Concurrency.ApiKeys.TryAcquire(token.ApiKeyId, token.MaxConcurrency)!;
        using (HttpResponseMessage busy = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal("rate_limit_exceeded", await ErrorCodeAsync(busy));
        }

        lease.Dispose();
        using (HttpResponseMessage free = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, free.StatusCode);
        }

        // 请求结束后位置还回去了。
        await WaitUntilAsync(() => Checker(host).Concurrency.ApiKeys.Current(token.ApiKeyId) == 0);
    }

    [Fact]
    public async Task AChannelOverItsDailyLimitLeavesRoutingWithoutCoolingDown()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            Configure((_, channels, _) => channels[0].DailyCostLimitNanoUsd = 1));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);
        int channelA = Snapshot(host).Channels.Single(c => c.ChannelName == "a").ChannelId;

        using (HttpResponseMessage first = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await WaitUntilAsync(() => Checker(host).Costs.ChannelToday(channelA) > 0);

        // a 今天到了上限：第二次直接走 b，a 不被打一次再换。
        using (HttpResponseMessage second = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        Assert.Equal(["sk-a", "sk-b"], host.Upstream.SecretsTried);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task WhenEveryChannelIsOverItsDailyLimitTheClientIsToldWhenTomorrowStarts()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, channels, _) => channels[0].DailyCostLimitNanoUsd = 1));
        host.Upstream.Script("sk-a", Ok);
        int channelA = Snapshot(host).Channels.Single().ChannelId;

        using (HttpResponseMessage first = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await WaitUntilAsync(() => Checker(host).Costs.ChannelToday(channelA) > 0);

        using HttpResponseMessage blocked = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter?.Delta);

        // 下一个本地午夜，不会超过一天。
        GatewayTimeZone zone = host.Services.GetRequiredService<GatewayTimeZone>();
        TimeSpan untilMidnight = zone.StartOf(zone.Today().AddDays(1)) - DateTimeOffset.UtcNow;
        Assert.InRange(blocked.Headers.RetryAfter!.Delta!.Value, untilMidnight - TimeSpan.FromSeconds(5), untilMidnight + TimeSpan.FromSeconds(5));

        RequestRecord record = (await host.ReadRecordsAsync())[1];
        Assert.Equal(FailureClass.RestrictionBlocked, record.BlockedBy);
        Assert.Equal("渠道 a：今日费用已达上限", record.BlockDetail);
        Assert.Empty(record.Attempts);
    }

    [Fact]
    public async Task AKeyThatUsedUpItsMinuteIsSkippedForTheNextKey()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a1", "sk-a2"])],
            Configure((_, channels, _) => channels[0].Keys.First(k => k.Name == "key0").RequestsPerMinute = 1));
        host.Upstream.Script("sk-a1", Ok);
        host.Upstream.Script("sk-a2", Ok);

        // 离整分钟太近的话，两次请求会落在不同的分钟桶里。
        if (DateTimeOffset.UtcNow.Second >= 57)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
        }

        using (HttpResponseMessage first = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        Assert.Equal(["sk-a1", "sk-a2"], host.Upstream.SecretsTried);
        Assert.Equal(1, Checker(host).Rates.CountThisMinute(SecretFingerprint.Compute("sk-a1")));
    }

    [Fact]
    public async Task AKeyAtItsConcurrencyLimitIsSkippedAndAllFullGivesNoRetryAfter()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, channels, _) => channels[0].Keys.Single().MaxConcurrency = 1));
        host.Upstream.Script("sk-a", Ok);
        string fingerprint = SecretFingerprint.Compute("sk-a");

        IDisposable lease = Checker(host).Concurrency.ChannelKeys.TryAcquire(fingerprint, 1)!;
        using (HttpResponseMessage busy = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);

            // 并发什么时候空出来算不出来，就不给 Retry-After（Q91）。
            Assert.Null(busy.Headers.RetryAfter);
        }

        lease.Dispose();
        using (HttpResponseMessage free = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, free.StatusCode);
        }

        await WaitUntilAsync(() => Checker(host).Concurrency.ChannelKeys.Current(fingerprint) == 0);
        RequestRecord blocked = (await host.ReadRecordsAsync())[0];
        Assert.Equal("渠道 a：渠道密钥的并发已满", blocked.BlockDetail);
    }

    [Fact]
    public async Task ACandidateWhoseKeysAreAllFullIsRecordedAsSkipped()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            Configure((_, channels, _) => channels[0].Keys.Single().MaxConcurrency = 1));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);
        string fingerprint = SecretFingerprint.Compute("sk-a");

        using (Checker(host).Concurrency.ChannelKeys.TryAcquire(fingerprint, 1))
        {
            using HttpResponseMessage served = await host.PostAsync(StreamBody);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }

        Assert.Equal(["sk-b"], host.Upstream.SecretsTried);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        SkippedCandidate skipped = Assert.Single(record.Skipped);
        Assert.Equal("a", skipped.ChannelName);
        Assert.Equal(BlockReason.Concurrency, skipped.Reason);
        Assert.Null(skipped.RecoversAtMs);
    }

    [Fact]
    public async Task AKeyWhitelistSendsOtherModelsToTheOtherKey()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a1", "sk-a2"])],
            Configure((_, channels, _) => channels[0].Keys.First(k => k.Name == "key0").AllowedModels.Add("claude-x")));
        host.Upstream.Script("sk-a1", Ok);
        host.Upstream.Script("sk-a2", Ok);

        using (HttpResponseMessage response = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (HttpResponseMessage response = await host.PostAsync("{\"model\":\"claude-x\",\"stream\":true,\"messages\":[]}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // gpt-4o 不在 key0 的白名单里，只能走 key1；claude-x 在，按优先级走 key0。
        Assert.Equal(["sk-a2", "sk-a1"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task AChannelOutsideItsTimeWindowIsSkipped()
    {
        // 选上除今天以外的每一天：不管现在几点，今天都关着。
        GatewayTimeZone probe = new(TimeProvider.System);
        int everyDayButToday = TimeWindowSchedule.AllWeekdays & ~(1 << (int)probe.ToLocal(DateTimeOffset.UtcNow).DayOfWeek);

        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            Configure((_, channels, _) => channels[0].TimeWindows.Add(
                new ChannelTimeWindow { Weekdays = everyDayButToday, StartMinute = 0, EndMinute = 0 })));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-b"], host.Upstream.SecretsTried);

        // 请求成功了也记下主力为什么没走（Q263）：事后看日志能知道是时段挡住的。
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        SkippedCandidate skipped = Assert.Single(record.Skipped);
        Assert.Equal("a", skipped.ChannelName);
        Assert.Equal("gpt-4o", skipped.UpstreamModel);
        Assert.Equal(BlockReason.OutsideTimeWindow, skipped.Reason);
        Assert.NotNull(skipped.RecoversAtMs);
    }

    [Fact]
    public async Task NothingIsRecordedAsSkippedWhenTheFirstChoiceServes()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            Configure((_, _, _) => { }));
        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Assert.Single(await host.ReadRecordsAsync()).Skipped);
    }

    [Fact]
    public async Task ApiKeyChannelListsNarrowTheCandidates()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 5, ["sk-b"]), ("c", 0, ["sk-c"])],
            Configure((_, channels, key) =>
            {
                // 白名单 a、b，再减去 a：只剩 b。
                key.AllowedChannelIds.AddRange([channels[0].Id, channels[1].Id]);
                key.BlockedChannelIds.Add(channels[0].Id);
            }));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);
        host.Upstream.Script("sk-c", Ok);

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-b"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task WhenTheApiKeyExcludesEveryChannelItIsARestrictionNotAMissingChannel()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, channels, key) => key.BlockedChannelIds.Add(channels[0].Id)));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.RestrictionBlocked, record.BlockedBy);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task AnUnpublishedModelRecordsWhyItWasRejected()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Configure());

        using HttpResponseMessage response = await host.PostAsync("{\"model\":\"nope\",\"stream\":true,\"messages\":[]}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal("模型没有下发", record.BlockDetail);
    }

    [Fact]
    public async Task TheModelListOnlyShowsWhatTheApiKeyMayUse()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, _, key) => key.AllowedModels.AddRange(["gpt-4o", "claude-x"])));

        using HttpResponseMessage response = await host.GetModelsAsync();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] ids = [.. body.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];

        Assert.Equal(["claude-x", "gpt-4o"], ids);
    }

    [Fact]
    public async Task TheModelListHidesModelsWhoseOnlyChannelsAreExcluded()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure((_, channels, key) => key.BlockedChannelIds.Add(channels[0].Id)));

        using HttpResponseMessage response = await host.GetModelsAsync();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task AFinishedRequestLeavesTheInFlightRegistry()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Configure());
        host.Upstream.Script("sk-a", Ok);

        using (HttpResponseMessage response = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        InFlightRegistry registry = host.Services.GetRequiredService<InFlightRegistry>();
        await WaitUntilAsync(() => registry.Snapshot().Count == 0);
    }
}
