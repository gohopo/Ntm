using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 切片 5 的计费：Usage × 单价 × 倍率落到最后那个产生了消耗的 Attempt 上（Q46/Q49），
/// 上游没给用量时按字符估（Q47），缺单价就是「无单价」而不是 0（Q65）。
/// </summary>
public sealed class PricingRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private const string StreamBody = "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}";

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    /// <summary>configure 回调不会自动下发模型：手动下发 gpt-4o，规则「所有渠道 + 精确名」。</summary>
    private static void PublishGpt4o(ConfigDbContext db)
    {
        var published = new PublishedModel { Name = "gpt-4o", Enabled = true };
        published.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = "gpt-4o" });
        db.PublishedModels.Add(published);
    }

    /// <summary>给渠道配上供应商，并为 (供应商, 上游模型) 填一行单价。</summary>
    private static Action<ConfigDbContext, IReadOnlyList<Channel>> WithPrice(
        decimal? input,
        decimal? output,
        string provider = "openai",
        string upstreamModel = "gpt-4o",
        decimal? thinkingOutput = null) =>
        (db, channels) =>
        {
            foreach (Channel channel in channels)
            {
                channel.Provider = provider;
                foreach (string model in RelayHost.Models)
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                }
            }

            PublishGpt4o(db);
            db.PriceRows.Add(new PriceRow
            {
                Provider = provider,
                UpstreamModel = upstreamModel,
                Source = PriceSource.Manual,
                InputPrice = input,
                OutputPrice = output,
                ThinkingOutputPrice = thinkingOutput,
            });
        };

    [Fact]
    public async Task UpstreamUsageIsPricedWithTheSnapshotAndTheMultiplier()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            WithPrice(input: 2.5m, output: 10m));

        // 倍率 1.5×：转卖渠道比原厂贵一半。
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Channel channel = db.Channels.Single();
            channel.MultiplierBasisPoints = 15_000;
            await db.SaveChangesAsync();
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);

        // 1000×2.5 + 200×10 = 4500 美元/百万 → $0.0045，再乘 1.5 = $0.00675 = 6_750_000 纳美元
        Assert.Equal(6_750_000, attempt.CostNanoUsd);
        Assert.Equal(6_750_000, record.CostNanoUsd);
        Assert.Equal(UsageSource.Upstream, attempt.UsageSource);
        Assert.Equal(2.5m, attempt.InputPrice);
        Assert.Equal(10m, attempt.OutputPrice);
        Assert.Null(attempt.LongContextOver);
    }

    [Fact]
    public async Task AFailedOverAttemptCarriesNoUsageAndNoCost()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            WithPrice(input: 2.5m, output: 10m));

        host.Upstream.Script("sk-a", ServerError);
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(2, record.Attempts.Count);

        // 被换掉的那次：没有用量，也没有费用（Q46）。
        Assert.Equal(FailureClass.UpstreamServerError, record.Attempts[0].FailureClass);
        Assert.Null(record.Attempts[0].CostNanoUsd);
        Assert.Equal(0, record.Attempts[0].InputTokens);
        Assert.Equal(UsageSource.Unavailable, record.Attempts[0].UsageSource);

        Assert.Equal(4_500_000, record.Attempts[1].CostNanoUsd);
        Assert.Equal(4_500_000, record.CostNanoUsd);
    }

    [Fact]
    public async Task AMissingUnitPriceLeavesTheAttemptUnpricedInsteadOfFree()
    {
        // 只有输入单价：输出用了 200 Token 却没有单价，整次算不出费用（Q65）。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            WithPrice(input: 2.5m, output: null));

        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);

        Assert.Null(attempt.CostNanoUsd);
        Assert.Null(record.CostNanoUsd);
        Assert.Equal(2.5m, attempt.InputPrice);
        Assert.Null(attempt.OutputPrice);
        Assert.Equal(1000, attempt.InputTokens);
    }

    [Fact]
    public async Task AChannelWithNoPriceRowIsUnpriced()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                foreach (Channel channel in channels)
                {
                    channel.Provider = "openai";
                    foreach (string model in RelayHost.Models)
                    {
                        channel.UpstreamModels.Add(
                            new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                    }
                }

                PublishGpt4o(db);
            });

        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);

        Assert.Null(attempt.CostNanoUsd);
        Assert.Null(attempt.InputPrice);
        Assert.Equal(1000, attempt.InputTokens);
    }

    [Fact]
    public async Task AStreamWithoutUsageIsEstimatedAndStillBilled()
    {
        // 上游一个 usage 块都没给（断开、或者上游就是不发）：按字符估，不能变成免费（Q46/Q47）。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            WithPrice(input: 2.5m, output: 10m));

        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);

        Assert.Equal(UsageSource.GatewayEstimated, attempt.UsageSource);
        Assert.Equal(UsageSource.GatewayEstimated, record.UsageSource);

        // 估出来的 Token 很少（请求体只有 model 和空 messages，输出是 "ok"），但不是 0，也算出了费用。
        Assert.True(attempt.InputTokens > 0, "输入应该估出至少 1 个 Token");
        Assert.Equal(1, attempt.OutputTokens);
        Assert.True(attempt.CostNanoUsd > 0, "估算出来的用量也要计费");
    }

    [Fact]
    public async Task AStreamThatEmittedThinkingBillsTheWholeOutputAtTheThinkingPrice()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            WithPrice(input: 0.4m, output: 1.2m, thinkingOutput: 4m));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想一下\"}}]}\n\n",
            Content,
            Finish,
            Usage,
            Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AttemptRecord attempt = Assert.Single(Assert.Single(await host.ReadRecordsAsync()).Attempts);

        // 1000×0.4 + 200×4 = 1200 美元/百万 → 1_200_000 纳美元：200 个输出 Token 全按思考价（ADR-0013）。
        Assert.True(attempt.SawThinking);
        Assert.Equal(4m, attempt.OutputPrice);
        Assert.Equal(1_200_000, attempt.CostNanoUsd);
    }

    [Fact]
    public async Task AStreamWithoutThinkingIgnoresTheThinkingPrice()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            WithPrice(input: 0.4m, output: 1.2m, thinkingOutput: 4m));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AttemptRecord attempt = Assert.Single(Assert.Single(await host.ReadRecordsAsync()).Attempts);
        Assert.False(attempt.SawThinking);
        Assert.Equal(1.2m, attempt.OutputPrice);
        Assert.Equal(640_000, attempt.CostNanoUsd);
    }
}
