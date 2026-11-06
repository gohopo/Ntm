using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Tests;

/// <summary>/api/stats：总览页的统计，全部从小时汇总算（Q43/Q54/Q55/Q60）。</summary>
public sealed class StatsApiTests
{
    private static readonly string[] Models = ["gpt-4o"];

    [Fact]
    public async Task StatsNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BadRangesAndTimeZonesAreRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode range, JsonElement problem) = await admin.GetAsync("/api/stats?range=1y");
        Assert.Equal(HttpStatusCode.BadRequest, range);
        Assert.Equal("时间范围只能是 24h、7d、30d 或 90d。", problem.GetProperty("errors").GetProperty("range")[0].GetString());

        (HttpStatusCode zone, _) = await admin.GetAsync("/api/stats?tz=Mars/Olympus");
        Assert.Equal(HttpStatusCode.BadRequest, zone);
    }

    [Fact]
    public async Task TheLastDayIsSummedIntoHoursAndGroups()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("alpha", Models);

        DateTimeOffset hour = CurrentHour();
        await SeedAsync(
            host,
            Row(hour, channel, "renamed-before", requests: 3, success: 2, failure: 1, cost: 5_000, buckets: (0, 2)),
            Row(hour.AddHours(-3), channel, "renamed-before", requests: 1, success: 1, cost: 1_000, buckets: (3, 1)),
            Row(hour.AddHours(-3), 999, "gone-channel", requests: 4, success: 4, cost: 9_000, apiKeyId: 77, apiKeyName: "old-token"),
            Row(hour.AddHours(-30), channel, "alpha", requests: 100, success: 100, cost: 1));

        JsonElement stats = await admin.GetJsonAsync("/api/stats");
        Assert.Equal("24h", stats.GetProperty("range").GetString());
        Assert.Equal("hour", stats.GetProperty("granularity").GetString());

        // 30 小时前那一行不在最近 24 小时里。
        JsonElement totals = stats.GetProperty("totals");
        Assert.Equal(8, totals.GetProperty("requestCount").GetInt64());
        Assert.Equal(7, totals.GetProperty("successCount").GetInt64());
        Assert.Equal(15_000, totals.GetProperty("costNanoUsd").GetInt64());
        Assert.Equal(3, totals.GetProperty("timeToFirstContentSampleCount").GetInt64());

        JsonElement[] points = [.. stats.GetProperty("points").EnumerateArray()];
        Assert.Equal(24, points.Length);
        Assert.True(points[^1].GetProperty("inProgress").GetBoolean());
        Assert.False(points[^2].GetProperty("inProgress").GetBoolean());
        Assert.Equal(2, points[^1].GetProperty("successCount").GetInt64());
        Assert.Equal(1, points[^1].GetProperty("failureCount").GetInt64());
        Assert.Equal(10_000, points[^4].GetProperty("costNanoUsd").GetInt64());

        // 渠道按 id 分组：还在的显示当前名字，删掉的显示最后记下的名字（Q60）。默认按费用降序。
        JsonElement[] channels = [.. stats.GetProperty("groups").GetProperty("channels").EnumerateArray()];
        Assert.Equal(2, channels.Length);
        Assert.Equal("gone-channel", channels[0].GetProperty("name").GetString());
        Assert.True(channels[0].GetProperty("deleted").GetBoolean());
        Assert.Equal("alpha", channels[1].GetProperty("name").GetString());
        Assert.False(channels[1].GetProperty("deleted").GetBoolean());
        Assert.Equal(channel.ToString(System.Globalization.CultureInfo.InvariantCulture), channels[1].GetProperty("key").GetString());

        JsonElement[] tokens = [.. stats.GetProperty("groups").GetProperty("apiKeys").EnumerateArray()];
        Assert.Contains(tokens, t => t.GetProperty("name").GetString() == "old-token" && t.GetProperty("deleted").GetBoolean());
        Assert.Contains(tokens, t => t.GetProperty("name").GetString() == "工作台");

        JsonElement alpha = channels[1].GetProperty("totals");
        Assert.Equal(4, alpha.GetProperty("requestCount").GetInt64());
        // 三个样本：两个在 <0.25s，一个在 1s–2s。第 1.5 名落在第一个桶的 3/4 处。
        Assert.Equal(188, alpha.GetProperty("timeToFirstContentP50Ms").GetInt64());
    }

    [Fact]
    public async Task DaysFollowTheBrowserTimeZone()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        TimeZoneInfo shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, shanghai).DateTime);
        DateTimeOffset todayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(8)).ToUniversalTime();

        // 当地今天零点那一小时算今天，前一小时（UTC 15:00）算昨天。
        await SeedAsync(
            host,
            Row(todayStart, 1, "alpha", requests: 2, success: 2),
            Row(todayStart.AddHours(-1), 1, "alpha", requests: 5, success: 5));

        JsonElement stats = await admin.GetJsonAsync("/api/stats?range=7d&tz=Asia/Shanghai");
        Assert.Equal("day", stats.GetProperty("granularity").GetString());
        Assert.Equal("Asia/Shanghai", stats.GetProperty("timeZone").GetString());

        JsonElement[] points = [.. stats.GetProperty("points").EnumerateArray()];
        Assert.Equal(7, points.Length);
        Assert.Equal(todayStart, points[^1].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(today.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture), points[^1].GetProperty("label").GetString());
        Assert.True(points[^1].GetProperty("inProgress").GetBoolean());
        Assert.Equal(2, points[^1].GetProperty("successCount").GetInt64());
        Assert.Equal(5, points[^2].GetProperty("successCount").GetInt64());
        Assert.Equal(todayStart.AddDays(-6), stats.GetProperty("from").GetDateTimeOffset());
    }

    private static DateTimeOffset CurrentHour()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
    }

    private static HourlyAggregate Row(
        DateTimeOffset hour,
        int channelId,
        string channelName,
        long requests = 0,
        long success = 0,
        long failure = 0,
        long cost = 0,
        int apiKeyId = 0,
        string? apiKeyName = null,
        (int Index, long Count) buckets = default)
    {
        var row = new HourlyAggregate
        {
            HourUtc = hour,
            ChannelId = channelId,
            ChannelName = channelName,
            PublishedModel = "gpt-4o",
            UpstreamModel = "gpt-4o",
            ApiKeyId = apiKeyId,
            ApiKeyName = apiKeyName,
            RequestCount = requests,
            SuccessCount = success,
            FailureCount = failure,
            AttemptCount = requests,
            CostNanoUsd = cost,
        };

        // 按下标把样本放进对应的桶，借用 AddTimeToFirstContent 保证计数和总和一致。
        long[] lowerBounds = [0, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 64_000];
        for (long i = 0; i < buckets.Count; i++)
        {
            row.AddTimeToFirstContent(lowerBounds[buckets.Index]);
        }

        return row;
    }

    private static async Task SeedAsync(ServerHost host, params HourlyAggregate[] rows)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        traffic.HourlyAggregates.AddRange(rows);
        await traffic.SaveChangesAsync();
    }
}
