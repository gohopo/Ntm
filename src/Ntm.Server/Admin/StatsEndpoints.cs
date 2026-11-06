using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Admin;

/// <summary>一组汇总数字。成功率、尝试失败率这类比例由前端算，后端只给计数。</summary>
/// <param name="CostNanoUsd">算得出费用的那部分，纳美元；无单价的请求另记在 <paramref name="UnpricedCount"/>。</param>
/// <param name="TimeToFirstContentP50Ms">从桶估出的中位首字时间；没有流式样本时为 null（Q45/Q58）。</param>
internal sealed record StatsTotalsView(
    long RequestCount,
    long SuccessCount,
    long FailureCount,
    long CanceledCount,
    long UnpricedCount,
    long EstimatedCount,
    long AttemptCount,
    long FailedAttemptCount,
    long InputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long OutputTokens,
    long CostNanoUsd,
    long TimeToFirstContentSampleCount,
    long? TimeToFirstContentP50Ms,
    long? TimeToFirstContentP95Ms);

/// <summary>趋势图上的一个点：一小时或当地的一天（Q54）。</summary>
/// <param name="Label">当地时间的标签，「10-28 14:00」或「10-28」，前端直接显示。</param>
/// <param name="InProgress">当前这一小时 / 这一天，还没过完。</param>
internal sealed record StatsPointView(
    DateTimeOffset Start,
    string Label,
    bool InProgress,
    long SuccessCount,
    long FailureCount,
    long CanceledCount,
    long CostNanoUsd,
    long UnpricedCount);

/// <summary>分组表的一行（Q55/Q60）。</summary>
/// <param name="Key">渠道 / API 密钥是 id，模型是名字。</param>
/// <param name="Deleted">渠道或 API 密钥已经删了，名字是汇总里最后记下的。</param>
internal sealed record StatsGroupView(string Key, string Name, bool Deleted, StatsTotalsView Totals);

internal sealed record StatsGroupsView(
    IReadOnlyList<StatsGroupView> PublishedModels,
    IReadOnlyList<StatsGroupView> Channels,
    IReadOnlyList<StatsGroupView> UpstreamModels,
    IReadOnlyList<StatsGroupView> ApiKeys);

/// <param name="Granularity">hour 或 day。</param>
internal sealed record StatsView(
    string Range,
    string TimeZone,
    string Granularity,
    DateTimeOffset From,
    DateTimeOffset To,
    StatsTotalsView Totals,
    IReadOnlyList<StatsPointView> Points,
    StatsGroupsView Groups);

/// <summary>
/// 总览页的统计（Q43/Q54/Q55）。全部从小时汇总算，不碰请求记录：记录有保留期，汇总是永久的账。
/// </summary>
internal static class StatsEndpoints
{
    private static readonly Dictionary<string, int> RangeDays = new(StringComparer.Ordinal)
    {
        ["24h"] = 0,
        ["7d"] = 7,
        ["30d"] = 30,
        ["90d"] = 90,
    };

    public static RouteGroupBuilder MapStatsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAsync);
        return group;
    }

    /// <param name="range">24h（默认）/ 7d / 30d / 90d。</param>
    /// <param name="tz">浏览器的 IANA 时区，例如 Asia/Shanghai；默认 UTC。天的边界按它算。</param>
    private static async Task<IResult> GetAsync(
        TrafficDbContext traffic,
        ConfigDbContext config,
        TimeProvider timeProvider,
        string? range = null,
        string? tz = null)
    {
        var errors = new ValidationErrors();
        string rangeKey = string.IsNullOrWhiteSpace(range) ? "24h" : range.Trim();
        if (!RangeDays.TryGetValue(rangeKey, out int days))
        {
            errors.Add("range", "时间范围只能是 24h、7d、30d 或 90d。");
        }

        TimeZoneInfo zone = TimeZoneInfo.Utc;
        string zoneId = string.IsNullOrWhiteSpace(tz) ? "UTC" : tz.Trim();
        if (zoneId != "UTC" && !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out zone!))
        {
            errors.Add("tz", "不认识这个时区。");
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Bucket> buckets = days == 0 ? Hours(now, zone) : Days(now, zone, days);
        DateTimeOffset from = buckets[0].Start;
        DateTimeOffset to = buckets[^1].End;

        IQueryable<HourlyAggregate> window = traffic.HourlyAggregates
            .AsNoTracking()
            .Where(a => a.HourUtc >= from && a.HourUtc < to);

        // 一次按小时求和，用来画趋势图；一次按四个维度求和，用来出分组表和合计。
        // 两次都在 SQLite 里聚合，读回来的行数是「小时数」和「组合数」，不是原始行数。
        var hourly = await window
            .GroupBy(a => a.HourUtc)
            .Select(g => new
            {
                Hour = g.Key,
                Success = g.Sum(a => a.SuccessCount),
                Failure = g.Sum(a => a.FailureCount),
                Canceled = g.Sum(a => a.CanceledCount),
                Cost = g.Sum(a => a.CostNanoUsd),
                Unpriced = g.Sum(a => a.UnpricedCount),
            })
            .ToListAsync()
            .ConfigureAwait(false);

        List<HourlyAggregate> combos = await window
            .GroupBy(a => new { a.ChannelId, a.PublishedModel, a.UpstreamModel, a.ApiKeyId })
            .Select(g => new HourlyAggregate
            {
                ChannelId = g.Key.ChannelId,
                ChannelName = "",
                PublishedModel = g.Key.PublishedModel,
                UpstreamModel = g.Key.UpstreamModel,
                ApiKeyId = g.Key.ApiKeyId,
                RequestCount = g.Sum(a => a.RequestCount),
                SuccessCount = g.Sum(a => a.SuccessCount),
                FailureCount = g.Sum(a => a.FailureCount),
                CanceledCount = g.Sum(a => a.CanceledCount),
                UnpricedCount = g.Sum(a => a.UnpricedCount),
                EstimatedCount = g.Sum(a => a.EstimatedCount),
                AttemptCount = g.Sum(a => a.AttemptCount),
                FailedAttemptCount = g.Sum(a => a.FailedAttemptCount),
                InputTokens = g.Sum(a => a.InputTokens),
                CacheReadTokens = g.Sum(a => a.CacheReadTokens),
                CacheWrite5mTokens = g.Sum(a => a.CacheWrite5mTokens),
                CacheWrite1hTokens = g.Sum(a => a.CacheWrite1hTokens),
                OutputTokens = g.Sum(a => a.OutputTokens),
                CostNanoUsd = g.Sum(a => a.CostNanoUsd),
                TimeToFirstContentUnder250ms = g.Sum(a => a.TimeToFirstContentUnder250ms),
                TimeToFirstContentUnder500ms = g.Sum(a => a.TimeToFirstContentUnder500ms),
                TimeToFirstContentUnder1s = g.Sum(a => a.TimeToFirstContentUnder1s),
                TimeToFirstContentUnder2s = g.Sum(a => a.TimeToFirstContentUnder2s),
                TimeToFirstContentUnder4s = g.Sum(a => a.TimeToFirstContentUnder4s),
                TimeToFirstContentUnder8s = g.Sum(a => a.TimeToFirstContentUnder8s),
                TimeToFirstContentUnder16s = g.Sum(a => a.TimeToFirstContentUnder16s),
                TimeToFirstContentUnder32s = g.Sum(a => a.TimeToFirstContentUnder32s),
                TimeToFirstContentUnder64s = g.Sum(a => a.TimeToFirstContentUnder64s),
                TimeToFirstContentOver64s = g.Sum(a => a.TimeToFirstContentOver64s),
                TimeToFirstContentMillisecondsSum = g.Sum(a => a.TimeToFirstContentMillisecondsSum),
                TimeToFirstContentSampleCount = g.Sum(a => a.TimeToFirstContentSampleCount),
            })
            .ToListAsync()
            .ConfigureAwait(false);

        // 趋势图：把小时行折进各自的桶。
        var points = new StatsPointView[buckets.Count];
        var sums = new long[buckets.Count, 5];
        foreach (var row in hourly)
        {
            int index = buckets.FindIndex(b => row.Hour >= b.Start && row.Hour < b.End);
            if (index < 0)
            {
                continue;
            }

            sums[index, 0] += row.Success;
            sums[index, 1] += row.Failure;
            sums[index, 2] += row.Canceled;
            sums[index, 3] += row.Cost;
            sums[index, 4] += row.Unpriced;
        }

        for (int i = 0; i < buckets.Count; i++)
        {
            Bucket bucket = buckets[i];
            points[i] = new StatsPointView(
                bucket.Start,
                bucket.Label,
                now >= bucket.Start && now < bucket.End,
                sums[i, 0],
                sums[i, 1],
                sums[i, 2],
                sums[i, 3],
                sums[i, 4]);
        }

        Names names = await NamesAsync(traffic, config, combos, from, to).ConfigureAwait(false);

        var groups = new StatsGroupsView(
            Group(combos, c => c.PublishedModel, key => (key, false)),
            Group(combos, c => c.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture), names.Channel),
            Group(combos, c => c.UpstreamModel, key => (key.Length == 0 ? "（没有上游模型）" : key, false)),
            Group(combos, c => c.ApiKeyId.ToString(System.Globalization.CultureInfo.InvariantCulture), names.ApiKey));

        return TypedResults.Ok(new StatsView(
            rangeKey,
            zoneId,
            days == 0 ? "hour" : "day",
            from,
            to,
            Totals(combos),
            points,
            groups));
    }

    private static List<StatsGroupView> Group(
        List<HourlyAggregate> combos,
        Func<HourlyAggregate, string> key,
        Func<string, (string Name, bool Deleted)> name) =>
        [
            .. combos
                .GroupBy(key, StringComparer.Ordinal)
                .Select(g =>
                {
                    (string display, bool deleted) = name(g.Key);
                    return new StatsGroupView(g.Key, display, deleted, Totals(g));
                })
                .OrderByDescending(g => g.Totals.CostNanoUsd)
                .ThenByDescending(g => g.Totals.RequestCount)
                .ThenBy(g => g.Name, StringComparer.Ordinal),
        ];

    private static StatsTotalsView Totals(IEnumerable<HourlyAggregate> rows)
    {
        var sum = new HourlyAggregate { ChannelName = "", PublishedModel = "", UpstreamModel = "" };
        foreach (HourlyAggregate row in rows)
        {
            sum.Add(row);
        }

        long[] buckets = sum.TimeToFirstContentBuckets();
        return new StatsTotalsView(
            sum.RequestCount,
            sum.SuccessCount,
            sum.FailureCount,
            sum.CanceledCount,
            sum.UnpricedCount,
            sum.EstimatedCount,
            sum.AttemptCount,
            sum.FailedAttemptCount,
            sum.InputTokens,
            sum.CacheReadTokens,
            sum.CacheWrite5mTokens,
            sum.CacheWrite1hTokens,
            sum.OutputTokens,
            sum.CostNanoUsd,
            sum.TimeToFirstContentSampleCount,
            TimeToFirstContentPercentile.Estimate(buckets, 0.5),
            TimeToFirstContentPercentile.Estimate(buckets, 0.95));
    }

    /// <summary>
    /// 渠道和 API 密钥的显示名（Q60）：还在的用当前名字，删掉的用汇总里最后记下的名字并标「已删除」。
    /// </summary>
    private static async Task<Names> NamesAsync(
        TrafficDbContext traffic,
        ConfigDbContext config,
        List<HourlyAggregate> combos,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        Dictionary<int, string> channels = await config.Channels
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name)
            .ConfigureAwait(false);
        Dictionary<int, string> tokens = await config.ApiKeys
            .AsNoTracking()
            .ToDictionaryAsync(t => t.Id, t => t.Name)
            .ConfigureAwait(false);

        // 删掉的那些去汇总里找最后一次记下的名字。一般没几个。
        int[] goneChannels = [.. combos.Select(c => c.ChannelId).Distinct().Where(id => id != 0 && !channels.ContainsKey(id))];
        int[] goneTokens = [.. combos.Select(c => c.ApiKeyId).Distinct().Where(id => id != 0 && !tokens.ContainsKey(id))];

        var lastChannelNames = new Dictionary<int, string>();
        if (goneChannels.Length > 0)
        {
            var rows = await traffic.HourlyAggregates
                .AsNoTracking()
                .Where(a => a.HourUtc >= from && a.HourUtc < to && goneChannels.Contains(a.ChannelId))
                .Select(a => new { a.ChannelId, a.ChannelName, a.HourUtc })
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.ChannelId))
            {
                lastChannelNames[group.Key] = group.MaxBy(r => r.HourUtc)!.ChannelName;
            }
        }

        var lastTokenNames = new Dictionary<int, string>();
        if (goneTokens.Length > 0)
        {
            var rows = await traffic.HourlyAggregates
                .AsNoTracking()
                .Where(a => a.HourUtc >= from && a.HourUtc < to && goneTokens.Contains(a.ApiKeyId))
                .Select(a => new { a.ApiKeyId, a.ApiKeyName, a.HourUtc })
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.ApiKeyId))
            {
                lastTokenNames[group.Key] = group.MaxBy(r => r.HourUtc)!.ApiKeyName ?? "#" + group.Key;
            }
        }

        return new Names(
            key =>
            {
                int id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                if (id == 0)
                {
                    return ("（没选到渠道）", false);
                }

                return channels.TryGetValue(id, out string? current)
                    ? (current, false)
                    : (lastChannelNames.GetValueOrDefault(id, "#" + key), true);
            },
            key =>
            {
                int id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                if (id == 0)
                {
                    // 不带 API 密钥的只有 Workbench 的请求：认证失败的请求不落记录（Q208）。
                    return ("工作台", false);
                }

                return tokens.TryGetValue(id, out string? current)
                    ? (current, false)
                    : (lastTokenNames.GetValueOrDefault(id, "#" + key), true);
            });
    }

    /// <summary>最近 24 个整小时，最后一个是当前这一小时（Q54）。</summary>
    private static List<Bucket> Hours(DateTimeOffset now, TimeZoneInfo zone)
    {
        var current = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var buckets = new List<Bucket>(24);
        for (int i = 23; i >= 0; i--)
        {
            DateTimeOffset start = current.AddHours(-i);
            DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);
            buckets.Add(new Bucket(start, start.AddHours(1), local.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    /// <summary>
    /// 最近 N 个当地日，最后一个是今天（Q43/Q54）。当地零点换成 UTC 后向下取整到整小时：
    /// 汇总只到小时，半小时时区（例如 Asia/Kolkata）的一天因此会差半小时。
    /// </summary>
    private static List<Bucket> Days(DateTimeOffset now, TimeZoneInfo zone, int days)
    {
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var buckets = new List<Bucket>(days);
        for (int i = days - 1; i >= 0; i--)
        {
            DateOnly day = today.AddDays(-i);
            buckets.Add(new Bucket(
                LocalMidnight(day, zone),
                LocalMidnight(day.AddDays(1), zone),
                day.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        DateTime local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // 少数时区在夏令时切换那天没有零点（直接从 23:59 跳到 01:00）：往后挪到第一个存在的整点。
        while (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        DateTimeOffset utc = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private sealed record Bucket(DateTimeOffset Start, DateTimeOffset End, string Label);

    private sealed record Names(
        Func<string, (string Name, bool Deleted)> Channel,
        Func<string, (string Name, bool Deleted)> ApiKey);
}
