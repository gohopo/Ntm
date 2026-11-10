using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Live;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Admin;

/// <summary>一段可用时段。<see cref="Weekdays"/> 用 0–6，周日是 0（同 <see cref="DayOfWeek"/>）；时间是 HH:mm，按网关时区。</summary>
internal sealed record TimeWindowView(IReadOnlyList<int> Weekdays, string Start, string End);

/// <summary>渠道上的 Restriction（Q77/Q81）。金额是纳美元。</summary>
internal sealed record ChannelRestrictionsView(long? DailyCostLimitNanoUsd, IReadOnlyList<TimeWindowView> TimeWindows);

/// <summary>渠道密钥上的 Restriction（Q79/Q80/Q82）。</summary>
internal sealed record KeyRestrictionsView(int? RequestsPerMinute, int? MaxConcurrency, IReadOnlyList<string> AllowedModels);

/// <summary>API 密钥上的 Restriction（Q77/Q80/Q83）。金额是纳美元。</summary>
internal sealed record ApiKeyRestrictionsView(
    long? CostLimitNanoUsd,
    int? MaxConcurrency,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<int> AllowedChannelIds,
    IReadOnlyList<int> BlockedChannelIds);

/// <summary>整体替换渠道的 Restriction。金额按美元填，最多 6 位小数（同单价页）；null 表示不限。</summary>
internal sealed record ChannelRestrictionsInput(decimal? DailyCostLimitUsd, IReadOnlyList<TimeWindowView?>? TimeWindows);

internal sealed record KeyRestrictionsInput(int? RequestsPerMinute, int? MaxConcurrency, IReadOnlyList<string?>? AllowedModels);

internal sealed record ApiKeyRestrictionsInput(
    decimal? CostLimitUsd,
    int? MaxConcurrency,
    IReadOnlyList<string?>? AllowedModels,
    IReadOnlyList<int>? AllowedChannelIds,
    IReadOnlyList<int>? BlockedChannelIds);

/// <summary>每日上限与终身上限都只在发出前比（Q107），所以「已花」可能略超「上限」。</summary>
internal sealed record ChannelRuntimeView(
    string TimeZone,
    long SpentTodayNanoUsd,
    long? DailyCostLimitNanoUsd,
    long UnpricedTodayCount,
    bool InTimeWindow,
    DateTimeOffset? NextWindowStart,
    int InFlight,
    IReadOnlyList<KeyRuntimeView> Keys,
    IReadOnlyList<HealthSample>? Health = null);

/// <summary>按密钥指纹计（Q21）：同一份密钥值挂在几个渠道上时，这里的数是它们合起来的。</summary>
internal sealed record KeyRuntimeView(int Id, int RequestsThisMinute, int InFlight);

internal sealed record ApiKeyRuntimeView(long SpentNanoUsd, long? CostLimitNanoUsd, long UnpricedCount, int InFlight);

/// <summary>每日上限与可用时段用的时区（Q86）。<see cref="IsFallback"/> 说明 TZ 设了却没解析成功，退回了 UTC。</summary>
internal sealed record TimeZoneView(string Id, string? Requested, bool IsFallback, DateTimeOffset Now, string LocalNow);

/// <summary>Restriction 的配置与运行状态（Q104）。渠道和 API 密钥两边的路由各自挂在自己的组下。</summary>
internal static class RestrictionEndpoints
{
    private const int MaxWindows = 20;
    private const int MaxListItems = 500;
    private const int MaxModelNameLength = 300;
    private const int MaxRequestsPerMinute = 1_000_000;
    private const int MaxConcurrency = 10_000;

    /// <summary>一百万美元：足够大，乘成纳美元也远在 long 范围内。</summary>
    private const decimal MaxCostLimitUsd = 1_000_000m;

    public static RouteGroupBuilder MapChannelRestrictionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/{id:int}/restrictions", SetChannelAsync);
        group.MapPut("/{id:int}/keys/{keyId:int}/restrictions", SetKeyAsync);
        group.MapGet("/{id:int}/runtime", ChannelRuntimeAsync);
        return group;
    }

    public static RouteGroupBuilder MapApiKeyRestrictionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/{id:int}/restrictions", SetApiKeyAsync);
        group.MapGet("/{id:int}/runtime", ApiKeyRuntimeAsync);
        return group;
    }

    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/time-zone", TimeZone);
        group.MapGet("/version", () => TypedResults.Ok(new VersionView(Hosting.BuildInfo.Version, Hosting.BuildInfo.Commit)));

        // 设置页顶上的提醒（Q352）。
        group.MapGet("/warnings", (Hosting.ForwardedHeadersMonitor forwarded) =>
            TypedResults.Ok(new SettingsWarningsView(forwarded.Detected)));
        return group;
    }

    public static ChannelRestrictionsView ToView(Channel channel) =>
        new(channel.DailyCostLimitNanoUsd, [.. channel.TimeWindows.Select(ToView)]);

    public static KeyRestrictionsView ToView(ChannelKey key) =>
        new(key.RequestsPerMinute, key.MaxConcurrency, key.AllowedModels);

    public static ApiKeyRestrictionsView ToView(ApiKey key) =>
        new(key.CostLimitNanoUsd, key.MaxConcurrency, key.AllowedModels, key.AllowedChannelIds, key.BlockedChannelIds);

    private static async Task<IResult> SetChannelAsync(
        int id,
        ChannelRestrictionsInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        var errors = new ValidationErrors();
        long? limit = ParseCostLimit(input.DailyCostLimitUsd, "dailyCostLimitUsd", errors);
        List<ChannelTimeWindow> windows = ParseWindows(input.TimeWindows, errors);
        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        // 改上限立即生效（Q106）：调低了下一个请求就被挡，调高或清空就放行。计数器不动。
        channel.DailyCostLimitNanoUsd = limit;
        channel.TimeWindows.Clear();
        channel.TimeWindows.AddRange(windows);
        channel.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(ToView(channel));
    }

    private static async Task<IResult> SetKeyAsync(
        int id,
        int keyId,
        KeyRestrictionsInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        ChannelKey? key = channel?.Keys.FirstOrDefault(k => k.Id == keyId);
        if (channel is null || key is null)
        {
            return AdminResults.NotFound("渠道密钥");
        }

        var errors = new ValidationErrors();
        int? rpm = ParseCount(input.RequestsPerMinute, MaxRequestsPerMinute, "requestsPerMinute", "每分钟请求数", errors);
        int? concurrency = ParseCount(input.MaxConcurrency, MaxConcurrency, "maxConcurrency", "并发上限", errors);

        // 上游模型名不校验是否在清单里：清单会随 ModelSync 变动，白名单不该因此存不进去。
        List<string> models = ParseNames(input.AllowedModels, "allowedModels", errors);
        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        key.RequestsPerMinute = rpm;
        key.MaxConcurrency = concurrency;
        key.AllowedModels.Clear();
        key.AllowedModels.AddRange(models);
        channel.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(ToView(key));
    }

    private static async Task<IResult> SetApiKeyAsync(
        int id,
        ApiKeyRestrictionsInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        var errors = new ValidationErrors();
        long? limit = ParseCostLimit(input.CostLimitUsd, "costLimitUsd", errors);
        int? concurrency = ParseCount(input.MaxConcurrency, MaxConcurrency, "maxConcurrency", "并发上限", errors);
        List<string> models = ParseNames(input.AllowedModels, "allowedModels", errors);
        List<int> allowed = [.. (input.AllowedChannelIds ?? []).Distinct()];
        List<int> blocked = [.. (input.BlockedChannelIds ?? []).Distinct()];

        if (allowed.Count > MaxListItems || blocked.Count > MaxListItems)
        {
            errors.Add("allowedChannelIds", "渠道名单太长了。");
        }

        // 渠道 id 要存在：手填的 id 写错了，名单就悄悄失效。下发模型名不校验，理由同密钥白名单。
        // 已经存着的 id 照收：渠道删了之后名单里会留下失效的 id（删渠道时故意不清），不能因此存不进去。
        HashSet<int> known = [.. await db.Channels.Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)];
        known.UnionWith(key.AllowedChannelIds);
        known.UnionWith(key.BlockedChannelIds);
        if (allowed.Any(c => !known.Contains(c)))
        {
            errors.Add("allowedChannelIds", "白名单里有不存在的渠道。");
        }

        if (blocked.Any(c => !known.Contains(c)))
        {
            errors.Add("blockedChannelIds", "黑名单里有不存在的渠道。");
        }

        if (allowed.Intersect(blocked).Any())
        {
            errors.Add("blockedChannelIds", "同一个渠道不能既在白名单里又在黑名单里。");
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        key.CostLimitNanoUsd = limit;
        key.MaxConcurrency = concurrency;
        key.AllowedModels.Clear();
        key.AllowedModels.AddRange(models);
        key.AllowedChannelIds.Clear();
        key.AllowedChannelIds.AddRange(allowed);
        key.BlockedChannelIds.Clear();
        key.BlockedChannelIds.AddRange(blocked);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(ToView(key));
    }

    private static async Task<IResult> ChannelRuntimeAsync(
        int id,
        ConfigDbContext db,
        TrafficDbContext traffic,
        RestrictionChecker restrictions,
        InFlightRegistry inFlight,
        HealthTracker health,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .AsNoTracking()
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        GatewayTimeZone zone = restrictions.TimeZone;
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset since = HourFloor(zone.StartOf(zone.Today()));

        // 无单价的请求不计入上限（Q89），只在页面上提醒有几条。
        long unpriced = channel.DailyCostLimitNanoUsd is null
            ? 0
            : await traffic.HourlyAggregates
                .Where(a => a.ChannelId == id && a.HourUtc >= since)
                .SumAsync(a => a.UnpricedCount, cancellationToken)
                .ConfigureAwait(false);

        List<TimeWindow> windows = [.. channel.TimeWindows.Select(w => new TimeWindow(w.Weekdays, w.StartMinute, w.EndMinute))];
        DateTime local = zone.ToLocal(now);
        bool open = TimeWindowSchedule.IsOpen(windows, local);
        DateTimeOffset? next = open ? null : TimeWindowSchedule.NextOpen(windows, local) is { } at ? zone.ToUtc(at) : null;

        return TypedResults.Ok(new ChannelRuntimeView(
            zone.Id,
            restrictions.Costs.ChannelToday(id),
            channel.DailyCostLimitNanoUsd,
            unpriced,
            open,
            next,
            inFlight.CountOnChannel(id),
            [.. channel.Keys.Select(k => new KeyRuntimeView(
                k.Id,
                restrictions.Rates.CountThisMinute(k.SecretFingerprint),
                restrictions.Concurrency.ChannelKeys.Current(k.SecretFingerprint)))],
            health.Samples(id)));
    }

    private static async Task<IResult> ApiKeyRuntimeAsync(
        int id,
        ConfigDbContext db,
        TrafficDbContext traffic,
        RestrictionChecker restrictions,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        long unpriced = key.CostLimitNanoUsd is null
            ? 0
            : await traffic.HourlyAggregates
                .Where(a => a.ApiKeyId == id)
                .SumAsync(a => a.UnpricedCount, cancellationToken)
                .ConfigureAwait(false);

        return TypedResults.Ok(new ApiKeyRuntimeView(
            restrictions.Costs.ApiKeyTotal(id),
            key.CostLimitNanoUsd,
            unpriced,
            restrictions.Concurrency.ApiKeys.Current(id)));
    }

    private static TimeZoneView TimeZone(GatewayTimeZone zone, TimeProvider timeProvider)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return new TimeZoneView(
            zone.Id,
            zone.Requested,
            zone.IsFallback,
            now,
            zone.ToLocal(now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset HourFloor(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, TimeSpan.Zero);

    private static TimeWindowView ToView(ChannelTimeWindow window) =>
        new(
            [.. Enumerable.Range(0, 7).Where(day => (window.Weekdays & (1 << day)) != 0)],
            FormatMinute(window.StartMinute),
            FormatMinute(window.EndMinute));

    private static string FormatMinute(int minute) =>
        (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);

    private static long? ParseCostLimit(decimal? usd, string field, ValidationErrors errors)
    {
        if (usd is not { } value)
        {
            return null;
        }

        if (value <= 0m || value > MaxCostLimitUsd)
        {
            errors.Add(field, "费用上限要大于 0、不超过 1000000 美元；不限就留空。");
            return null;
        }

        // 金额统一 6 位小数（Q61/Q63），多出来的精度存了也显示不出来。
        if (decimal.Truncate(value * 1_000_000m) != value * 1_000_000m)
        {
            errors.Add(field, "费用上限最多 6 位小数。");
            return null;
        }

        return (long)(value * 1_000_000_000m);
    }

    private static int? ParseCount(int? value, int max, string field, string label, ValidationErrors errors)
    {
        if (value is not { } count)
        {
            return null;
        }

        if (count < 1 || count > max)
        {
            errors.Add(field, label + "要在 1 到 " + max.ToString(CultureInfo.InvariantCulture) + " 之间；不限就留空。");
            return null;
        }

        return count;
    }

    private static List<string> ParseNames(IReadOnlyList<string?>? raw, string field, ValidationErrors errors)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (raw is null)
        {
            return names;
        }

        if (raw.Count > MaxListItems)
        {
            errors.Add(field, "模型名单太长了。");
            return names;
        }

        for (int i = 0; i < raw.Count; i++)
        {
            string name = raw[i]?.Trim() ?? "";
            if (name.Length == 0)
            {
                continue;
            }

            if (name.Length > MaxModelNameLength)
            {
                errors.Add(AdminResults.Item(field, i), "模型名最多 300 个字符。");
                continue;
            }

            // 精确匹配、大小写敏感（Q82）：和上游、和客户端请求里的模型名一致。
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<ChannelTimeWindow> ParseWindows(IReadOnlyList<TimeWindowView?>? raw, ValidationErrors errors)
    {
        var windows = new List<ChannelTimeWindow>();
        if (raw is null)
        {
            return windows;
        }

        if (raw.Count > MaxWindows)
        {
            errors.Add("timeWindows", "可用时段最多 20 段。");
            return windows;
        }

        for (int i = 0; i < raw.Count; i++)
        {
            string prefix = AdminResults.Item("timeWindows", i);
            TimeWindowView? window = raw[i];
            if (window is null)
            {
                errors.Add(prefix, "时段不能为空。");
                continue;
            }

            int weekdays = 0;
            foreach (int day in window.Weekdays ?? [])
            {
                if (day is < 0 or > 6)
                {
                    errors.Add(prefix + ".weekdays", "星期要在 0（周日）到 6 之间。");
                    weekdays = -1;
                    break;
                }

                weekdays |= 1 << day;
            }

            if (weekdays == 0)
            {
                errors.Add(prefix + ".weekdays", "至少选一天。");
            }

            int? start = ParseMinute(window.Start);
            int? end = ParseMinute(window.End);
            if (start is null)
            {
                errors.Add(prefix + ".start", "开始时间要写成 HH:mm。");
            }

            if (end is null)
            {
                errors.Add(prefix + ".end", "结束时间要写成 HH:mm。");
            }

            if (weekdays > 0 && start is { } s && end is { } e)
            {
                // 开始大于结束是跨午夜，开始等于结束是全天（Q81）。
                windows.Add(new ChannelTimeWindow { Weekdays = weekdays, StartMinute = s, EndMinute = e });
            }
        }

        return windows;
    }

    private static int? ParseMinute(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time)
            ? (time.Hour * 60) + time.Minute
            : null;
}

internal sealed record VersionView(string Version, string? Commit);

/// <summary>没配可信代理却收到了转发头（Q352）。</summary>
internal sealed record SettingsWarningsView(bool UntrustedForwardedHeaders);
