using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Tests;

/// <summary>/api/prices：单价行的列表、编辑、恢复自动与删除（Q48/Q65/Q66/Q70/Q74）。</summary>
public sealed class PriceApiTests
{
    private static readonly string[] AlphaModels = ["gpt-4o", "GPT-4o-mini"];
    private static readonly string[] BetaModels = ["gpt-4o"];

    [Fact]
    public async Task PricesNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/prices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EveryChannelModelGetsARowAndUnpricedRowsComeFirst()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int alpha = await admin.CreateChannelAsync("alpha", AlphaModels);
        await admin.CreateChannelAsync("beta", BetaModels);

        JsonElement page = await admin.GetJsonAsync("/api/prices");
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());

        JsonElement[] items = [.. page.GetProperty("items").EnumerateArray()];

        // 目录还没取到：两行都是空的，按 Provider、名字排。名字统一小写。
        Assert.All(items, item => Assert.True(item.GetProperty("unpriced").GetBoolean()));
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], items.Select(i => i.GetProperty("upstreamModel").GetString()));

        // 同一个 (openai, gpt-4o) 被两个渠道用着。
        JsonElement shared = items[0];
        Assert.Equal("openai", shared.GetProperty("provider").GetString());
        Assert.Equal(["alpha", "beta"], shared.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("auto", shared.GetProperty("source").GetString());

        // 近 7 天的无单价次数从汇总来，按渠道当前的 Provider 归行。
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            traffic.HourlyAggregates.Add(new HourlyAggregate
            {
                HourUtc = DateTimeOffset.UtcNow.AddHours(-2),
                ChannelId = alpha,
                ChannelName = "alpha",
                PublishedModel = "mini",
                UpstreamModel = "GPT-4o-mini",
                RequestCount = 3,
                UnpricedCount = 3,
            });
            traffic.HourlyAggregates.Add(new HourlyAggregate
            {
                HourUtc = DateTimeOffset.UtcNow.AddDays(-9),
                ChannelId = alpha,
                ChannelName = "alpha",
                PublishedModel = "mini",
                UpstreamModel = "GPT-4o-mini",
                RequestCount = 50,
                UnpricedCount = 50,
            });
            await traffic.SaveChangesAsync();
        }

        items = [.. (await admin.GetJsonAsync("/api/prices")).GetProperty("items").EnumerateArray()];
        Assert.Equal("gpt-4o-mini", items[0].GetProperty("upstreamModel").GetString());
        Assert.Equal(3, items[0].GetProperty("unpricedRequests").GetInt64());

        JsonElement searched = await admin.GetJsonAsync("/api/prices?search=MINI");
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task EditingARowMakesItManualAndRestoringAutoRematches()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateChannelAsync("alpha", BetaModels);
        int id = await FirstRowIdAsync(admin);

        (HttpStatusCode status, JsonElement row) = await admin.PutAsync("/api/prices/" + id, new
        {
            inputPrice = 2.5m,
            cacheReadPrice = 0m,
            outputPrice = 10m,
            longContext = new[]
            {
                new { inputTokensOver = 200_000L, prices = new { inputPrice = 5m, outputPrice = 15m } },
            },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("manual", row.GetProperty("source").GetString());
        Assert.False(row.GetProperty("unpriced").GetBoolean());

        // 0 是免费，空是不知道（Q65）：两者要原样存回来。
        JsonElement prices = row.GetProperty("prices");
        Assert.Equal(2.5m, prices.GetProperty("inputPrice").GetDecimal());
        Assert.Equal(0m, prices.GetProperty("cacheReadPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, prices.GetProperty("cacheWrite5mPrice").ValueKind);
        Assert.Equal(200_000, row.GetProperty("longContext")[0].GetProperty("inputTokensOver").GetInt64());

        JsonElement manual = await admin.GetJsonAsync("/api/prices?filter=manual");
        Assert.Equal(1, manual.GetProperty("total").GetInt32());

        // 目录是空的：恢复自动就配不上，回到无单价。
        (HttpStatusCode restored, JsonElement auto) = await admin.PostAsync("/api/prices/" + id + "/auto", new { });
        Assert.Equal(HttpStatusCode.OK, restored);
        Assert.Equal("auto", auto.GetProperty("source").GetString());
        Assert.True(auto.GetProperty("unpriced").GetBoolean());
        Assert.Equal(0, auto.GetProperty("longContext").GetArrayLength());
    }

    [Fact]
    public async Task BadPricesAndThresholdsAreRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateChannelAsync("alpha", BetaModels);
        int id = await FirstRowIdAsync(admin);

        (HttpStatusCode status, JsonElement problem) = await admin.PutAsync("/api/prices/" + id, new
        {
            inputPrice = -1m,
            outputPrice = 0.1234567m,
            longContext = new[]
            {
                new { inputTokensOver = 200_000L, prices = new { inputPrice = 5m } },
                new { inputTokensOver = 100_000L, prices = new { inputPrice = 6m } },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        JsonElement errors = problem.GetProperty("errors");
        Assert.Equal("单价不能是负数。", errors.GetProperty("inputPrice")[0].GetString());
        Assert.Equal("单价最多 6 位小数。", errors.GetProperty("outputPrice")[0].GetString());
        Assert.Equal("门槛要比上一组大。", errors.GetProperty("longContext[1].inputTokensOver")[0].GetString());

        (HttpStatusCode missing, _) = await admin.PutAsync("/api/prices/9999", new { inputPrice = 1m });
        Assert.Equal(HttpStatusCode.NotFound, missing);

        (HttpStatusCode badFilter, _) = await admin.GetAsync("/api/prices?filter=cheap");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter);
    }

    [Fact]
    public async Task OnlyUnusedRowsCanBeDeleted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("alpha", AlphaModels);
        int id = await FirstRowIdAsync(admin);

        Assert.Equal(HttpStatusCode.Conflict, await admin.DeleteAsync("/api/prices/" + id));

        // 上游模型删掉以后行还在（填过的单价不丢），这时候它没被使用，可以删。
        (HttpStatusCode updated, _) = await admin.PutAsync(AdminClient.Channel(channel), new
        {
            name = "alpha",
            baseUrl = "https://alpha.example/v1",
            provider = "openai",
            upstreamModels = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, updated);

        JsonElement unused = await admin.GetJsonAsync("/api/prices?filter=unused");
        Assert.Equal(2, unused.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync("/api/prices/" + id));
        Assert.Equal(HttpStatusCode.NotFound, await admin.DeleteAsync("/api/prices/" + id));
    }

    private static async Task<int> FirstRowIdAsync(AdminClient admin) =>
        (await admin.GetJsonAsync("/api/prices")).GetProperty("items")[0].GetProperty("id").GetInt32();
}
