using System.Net;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 汇总表（Q44/Q45/Q57/Q60/Q73）：跑真实请求，看写入器记下的小时行。
/// </summary>
public sealed class AggregateRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private const string StreamBody = "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}";
    private const string NonStreamBody = "{\"model\":\"gpt-4o\",\"messages\":[]}";

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    private static HttpResponseMessage NonStreamOk() =>
        ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"id\":\"x\",\"object\":\"chat.completion\",\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,"
            + "\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}");

    [Fact]
    public async Task ASuccessfulStreamIsCountedWithItsPublishedModelApiKeyAndFirstContentBucket()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HourlyAggregate row = Assert.Single(await host.ReadAggregatesAsync());
        Assert.Equal("gpt-4o", row.PublishedModel);
        Assert.Equal("gpt-4o", row.UpstreamModel);
        Assert.Equal("a", row.ChannelName);
        Assert.Equal("test", row.ApiKeyName);
        Assert.Equal(1, row.RequestCount);
        Assert.Equal(1, row.SuccessCount);
        Assert.Equal(0, row.FailedAttemptCount);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(200, row.OutputTokens);

        // 渠道没配 Provider，也就没有单价：用了 Token 算不出费用 = 无单价（Q44）。
        Assert.Equal(1, row.UnpricedCount);
        Assert.Equal(0, row.CostNanoUsd);

        // 本地伪上游几毫秒就回，落在最快的桶里。
        Assert.Equal(1, row.TimeToFirstContentSampleCount);
        Assert.Equal(1, row.TimeToFirstContentUnder250ms);
    }

    [Fact]
    public async Task ANonStreamRequestHasNoFirstContentSample()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", NonStreamOk);

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HourlyAggregate row = Assert.Single(await host.ReadAggregatesAsync());
        Assert.Equal(1, row.SuccessCount);
        Assert.Equal(0, row.TimeToFirstContentSampleCount);
        Assert.Equal(0L, row.TimeToFirstContentBuckets().Sum());
    }

    [Fact]
    public async Task AFailedOverChannelShowsAnAttemptFailureEvenThoughTheRequestSucceeded()
    {
        await using RelayHost host = await RelayHost.StartAsync(("primary", 10, ["sk-p"]), ("backup", 0, ["sk-b"]));
        host.Upstream.Script("sk-p", ServerError);
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<HourlyAggregate> rows = await host.ReadAggregatesAsync();
        HourlyAggregate primary = rows.Single(r => r.ChannelName == "primary");
        HourlyAggregate backup = rows.Single(r => r.ChannelName == "backup");

        // 请求算在备用渠道上，但主渠道的尝试失败率不能因为被换掉就看不见（Q57）。
        Assert.Equal(0, primary.RequestCount);
        Assert.Equal(1, primary.AttemptCount);
        Assert.Equal(1, primary.FailedAttemptCount);
        Assert.Equal(1, backup.RequestCount);
        Assert.Equal(1, backup.SuccessCount);
        Assert.Equal(0, backup.FailedAttemptCount);
    }

    [Fact]
    public async Task AnUnpublishedModelStillCountsAsAFailedRequestWithNoChannel()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));

        using HttpResponseMessage response = await host.PostAsync("{\"model\":\"nope\",\"stream\":true,\"messages\":[]}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // 建了记录却没有 Attempt：ChannelId 0、上游模型留空，计入请求数与失败数（Q73）。
        HourlyAggregate row = Assert.Single(await host.ReadAggregatesAsync());
        Assert.Equal(0, row.ChannelId);
        Assert.Equal(string.Empty, row.UpstreamModel);
        Assert.Equal("nope", row.PublishedModel);
        Assert.Equal(1, row.RequestCount);
        Assert.Equal(1, row.FailureCount);
        Assert.Equal(0, row.AttemptCount);
    }

    [Fact]
    public async Task PricedAndEstimatedRequestsAreCountedSeparately()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                channels[0].Provider = "openai";
                channels[0].UpstreamModels.Add(new UpstreamModel { Name = "gpt-4o", Source = UpstreamModelSource.Manual });
                var published = new PublishedModel { Name = "gpt-4o", Enabled = true };
                published.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = "gpt-4o" });
                db.PublishedModels.Add(published);
                db.PriceRows.Add(new PriceRow
                {
                    Provider = "openai",
                    UpstreamModel = "gpt-4o",
                    Source = PriceSource.Manual,
                    InputPrice = 2.5m,
                    OutputPrice = 10m,
                });
            });

        // 一次上游给了用量，一次没给（要估算）。
        var replies = new Queue<Func<HttpResponseMessage>>(
        [
            () => ScriptedUpstream.Sse(Content, Finish, Usage, Done),
            () => ScriptedUpstream.Sse(Content, Finish, Done),
        ]);
        host.Upstream.Script("sk-a", () => replies.Dequeue()());

        using (HttpResponseMessage first = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PostAsync(StreamBody))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        HourlyAggregate row = Assert.Single(await host.ReadAggregatesAsync());
        Assert.Equal(2, row.RequestCount);
        Assert.Equal(1, row.EstimatedCount);
        Assert.Equal(0, row.UnpricedCount);

        // 第一次 1000×2.5 + 200×10 = $0.0045；第二次是估算出来的一点点，也计进去了。
        Assert.True(row.CostNanoUsd > 4_500_000, $"费用 {row.CostNanoUsd} 应该包含估算的那一次");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(249, 0)]
    [InlineData(250, 1)]
    [InlineData(999, 2)]
    [InlineData(1_000, 3)]
    [InlineData(63_999, 8)]
    [InlineData(64_000, 9)]
    [InlineData(600_000, 9)]
    public void FirstContentTimesLandInTheRightBucket(long milliseconds, int bucket)
    {
        var row = new HourlyAggregate { ChannelName = "c", PublishedModel = "m", UpstreamModel = "u" };

        row.AddTimeToFirstContent(milliseconds);

        long[] buckets = row.TimeToFirstContentBuckets();
        Assert.Equal(1, buckets[bucket]);
        Assert.Equal(1, buckets.Sum());
        Assert.Equal(milliseconds, row.TimeToFirstContentMillisecondsSum);
    }
}
