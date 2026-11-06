using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Server.Tests;

public sealed class RequestApiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RequestsNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PagingWalksEveryRowOnceEvenWhenStartTimesTie()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        List<Guid> seeded =
        [
            await SeedAsync(host, T0),
            await SeedAsync(host, T0),
            await SeedAsync(host, T0),
            await SeedAsync(host, T0.AddSeconds(-1)),
            await SeedAsync(host, T0.AddSeconds(-2)),
        ];

        var seen = new List<Guid>();
        var startTimes = new List<DateTimeOffset>();
        string? cursor = null;
        int pages = 0;
        do
        {
            string url = "/api/requests?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            JsonElement page = await admin.GetJsonAsync(url);
            foreach (JsonElement row in page.GetProperty("items").EnumerateArray())
            {
                seen.Add(row.GetProperty("id").GetGuid());
                startTimes.Add(row.GetProperty("startedAt").GetDateTimeOffset());
            }

            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(seeded.Order(), seen.Order());
        Assert.Equal(startTimes.OrderDescending(), startTimes);
    }

    [Fact]
    public async Task FiltersAndBadQueriesAreHandled()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        Guid failed = await SeedAsync(host, T0, model: "gpt-4.1", status: RequestStatus.Failed, apiKeyId: 7);
        await SeedAsync(host, T0.AddSeconds(1), model: "gpt-4.1");
        await SeedAsync(host, T0.AddSeconds(2), model: "o3-mini", apiKeyId: 7);

        JsonElement byStatus = await admin.GetJsonAsync("/api/requests?status=Failed");
        Assert.Equal([failed], byStatus.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal("failed", byStatus.GetProperty("items")[0].GetProperty("status").GetString());

        JsonElement byModel = await admin.GetJsonAsync("/api/requests?model=gpt-4.1");
        Assert.Equal(2, byModel.GetProperty("items").GetArrayLength());

        JsonElement byToken = await admin.GetJsonAsync("/api/requests?apiKeyId=7");
        Assert.Equal(2, byToken.GetProperty("items").GetArrayLength());
        Assert.Null(byToken.GetProperty("nextCursor").GetString());

        (HttpStatusCode badStatus, JsonElement badStatusBody) = await admin.GetAsync("/api/requests?status=1");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Equal("没有这种状态。", badStatusBody.GetProperty("errors").GetProperty("status")[0].GetString());

        (HttpStatusCode badCursor, JsonElement badCursorBody) = await admin.GetAsync("/api/requests?cursor=abc");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor);
        Assert.Equal("翻页游标无效。", badCursorBody.GetProperty("errors").GetProperty("cursor")[0].GetString());

        (HttpStatusCode badLimit, _) = await admin.GetAsync("/api/requests?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit);
    }

    [Fact]
    public async Task TheDetailListsAttemptsInOrderAndTheRowShowsTheLastOne()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        Guid id = await SeedAsync(host, T0, attempts: 2);

        JsonElement detail = await admin.GetJsonAsync("/api/requests/" + id);
        JsonElement attempts = detail.GetProperty("attempts");
        Assert.Equal(2, attempts.GetArrayLength());
        Assert.Equal("upstreamServerError", attempts[0].GetProperty("failureClass").GetString());
        Assert.False(attempts[0].GetProperty("committed").GetBoolean());
        Assert.Equal("channel-2", attempts[1].GetProperty("channelName").GetString());
        Assert.Equal(1500, detail.GetProperty("durationMs").GetInt64());
        Assert.Equal(400, detail.GetProperty("timeToFirstContentMs").GetInt64());

        JsonElement row = (await admin.GetJsonAsync("/api/requests")).GetProperty("items")[0];
        Assert.Equal("channel-2", row.GetProperty("channelName").GetString());
        Assert.Equal("upstream-2", row.GetProperty("upstreamModel").GetString());
        Assert.Equal(2, row.GetProperty("attemptCount").GetInt32());

        (HttpStatusCode missing, _) = await admin.GetAsync("/api/requests/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task ANonStreamRequestHasNoTimeToFirstContent()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        Guid id = await SeedAsync(host, T0, isStream: false);

        JsonElement detail = await admin.GetJsonAsync("/api/requests/" + id);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("timeToFirstContentMs").ValueKind);
        Assert.Equal(1500, detail.GetProperty("durationMs").GetInt64());

        JsonElement row = (await admin.GetJsonAsync("/api/requests")).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("timeToFirstContentMs").ValueKind);
    }

    [Fact]
    public async Task TheCostAndItsUnitPricesAreReported()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        // 10 输入 × $2.5 + 20 输出 × $10 = $0.000225 = 225_000 纳美元。
        Guid priced = await SeedAsync(host, T0, costNanoUsd: 225_000);
        Guid unpriced = await SeedAsync(host, T0.AddMinutes(1));

        JsonElement rows = (await admin.GetJsonAsync("/api/requests")).GetProperty("items");
        JsonElement unpricedRow = rows[0];
        JsonElement pricedRow = rows[1];

        Assert.Equal(225_000, pricedRow.GetProperty("costNanoUsd").GetInt64());
        Assert.False(pricedRow.GetProperty("unpriced").GetBoolean());

        // 用了 Token 却没有费用 = 无单价，不是免费。
        Assert.Equal(JsonValueKind.Null, unpricedRow.GetProperty("costNanoUsd").ValueKind);
        Assert.True(unpricedRow.GetProperty("unpriced").GetBoolean());

        JsonElement detail = await admin.GetJsonAsync("/api/requests/" + priced);
        Assert.Equal(225_000, detail.GetProperty("costNanoUsd").GetInt64());
        JsonElement attempt = detail.GetProperty("attempts")[0];
        Assert.Equal(225_000, attempt.GetProperty("costNanoUsd").GetInt64());
        Assert.Equal(2.5m, attempt.GetProperty("inputPrice").GetDecimal());
        Assert.Equal(10m, attempt.GetProperty("outputPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("cacheReadPrice").ValueKind);
        Assert.Equal(10_000, attempt.GetProperty("multiplierBasisPoints").GetInt32());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("longContextOver").ValueKind);
    }

    [Fact]
    public async Task TheListFiltersByOriginAndShowsIt()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        Guid client = await SeedAsync(host, T0, apiKeyId: 7);
        Guid workbench = await SeedAsync(host, T0.AddSeconds(-1), origin: RequestOrigin.Workbench);

        JsonElement all = await admin.GetJsonAsync("/api/requests");
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());

        JsonElement onlyWorkbench = await admin.GetJsonAsync("/api/requests?origin=workbench");
        JsonElement row = Assert.Single(onlyWorkbench.GetProperty("items").EnumerateArray());
        Assert.Equal(workbench, row.GetProperty("id").GetGuid());
        Assert.Equal("workbench", row.GetProperty("origin").GetString());

        JsonElement onlyClient = await admin.GetJsonAsync("/api/requests?origin=client");
        Assert.Equal(client, Assert.Single(onlyClient.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        JsonElement detail = await admin.GetJsonAsync("/api/requests/" + workbench);
        Assert.Equal("workbench", detail.GetProperty("origin").GetString());

        (HttpStatusCode bad, _) = await admin.GetAsync("/api/requests?origin=nope");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    private static async Task<Guid> SeedAsync(
        ServerHost host,
        DateTimeOffset startedAt,
        string model = "gpt-4.1",
        RequestStatus status = RequestStatus.Succeeded,
        int? apiKeyId = null,
        int attempts = 1,
        bool isStream = true,
        long? costNanoUsd = null,
        RequestOrigin origin = RequestOrigin.Client)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();

        var record = new RequestRecord
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMilliseconds(1500),
            CommittedAt = startedAt.AddMilliseconds(400),
            ApiKeyId = apiKeyId,
            ApiKeyName = apiKeyId is null ? null : "token-" + apiKeyId,
            Origin = origin,
            PublishedModel = model,
            InboundApiFormat = ApiFormat.OpenAiChat,
            RequestKind = RequestKind.Chat,
            IsStream = isStream,
            Status = status,
            HttpStatusCode = 200,
            AttemptCount = attempts,
            InputTokens = 10,
            OutputTokens = 20,
            CostNanoUsd = costNanoUsd,
        };

        for (int i = 1; i <= attempts; i++)
        {
            bool last = i == attempts;
            record.Attempts.Add(new AttemptRecord
            {
                Ordinal = i,
                StartedAt = startedAt.AddMilliseconds(i * 100),
                FinishedAt = startedAt.AddMilliseconds(i * 100 + 50),
                ChannelId = i,
                ChannelName = "channel-" + i,
                ChannelKeyId = i,
                ChannelKeyFingerprint = new string('0', 64),
                ChannelKeyPrefix = "sk-",
                UpstreamApiFormat = ApiFormat.OpenAiChat,
                UpstreamModel = "upstream-" + i,
                RequestUri = "https://channel-" + i + ".example/v1/chat/completions",
                HttpStatusCode = last ? 200 : 502,
                FailureClass = last ? FailureClass.None : FailureClass.UpstreamServerError,
                Committed = last,
                InputTokens = last ? 10 : 0,
                OutputTokens = last ? 20 : 0,
                CostNanoUsd = last ? costNanoUsd : null,
                InputPrice = last && costNanoUsd is not null ? 2.5m : null,
                OutputPrice = last && costNanoUsd is not null ? 10m : null,
                MultiplierBasisPoints = 10_000,
            });
        }

        traffic.Requests.Add(record);
        await traffic.SaveChangesAsync();
        return record.Id;
    }
}
