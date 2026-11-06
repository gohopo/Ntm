namespace Ntm.Gateway.Restrictions;

/// <summary>
/// 网关时区：渠道的「每日」费用上限和可用时段都按它（Q76/Q86/Q105）。
/// 时区来自 <see cref="TimeProvider.LocalTimeZone"/>，生产上就是进程的 TZ 环境变量；测试换成别的时区不用改环境。
/// 改 TZ 要重启才生效。
/// </summary>
public sealed class GatewayTimeZone
{
    private readonly TimeProvider _time;

    public GatewayTimeZone(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _time = timeProvider;

        // 只有真实时钟才看 TZ：设了 TZ 却解析成 UTC，多半是镜像里没有 tzdata，或者 TZ 写错了。
        // .NET 这时不报错，静默退回 UTC——必须在启动日志和设置页上说出来，否则「每日」悄悄错了几个小时。
        if (ReferenceEquals(timeProvider, TimeProvider.System))
        {
            string? requested = Environment.GetEnvironmentVariable("TZ")?.TrimStart(':');
            Requested = string.IsNullOrWhiteSpace(requested) ? null : requested;
            IsFallback = Requested is not null
                && !IsUtcName(Requested)
                && IsUtcName(timeProvider.LocalTimeZone.Id);
        }
    }

    /// <summary>TZ 环境变量的原值（去掉前导冒号）。没设时为 null。</summary>
    public string? Requested { get; }

    /// <summary>设了 TZ 却没能用上，退回了 UTC。</summary>
    public bool IsFallback { get; }

    public TimeZoneInfo Zone => _time.LocalTimeZone;

    public string Id => Zone.Id;

    /// <summary>某个时刻在网关时区里的墙上时间。</summary>
    public DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, Zone);

    /// <summary>某个时刻在网关时区里是哪一天。</summary>
    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant));

    public DateOnly Today() => DateOf(_time.GetUtcNow());

    /// <summary>
    /// 网关时区里某天 0 点对应的 UTC 时刻。0 点不存在（夏令时跳过）时取那之后第一个存在的时刻。
    /// </summary>
    public DateTimeOffset StartOf(DateOnly date) => ToUtc(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>把网关时区的墙上时间换成 UTC。不存在的时刻往后挪到存在为止；有歧义的取较早的那个。</summary>
    public DateTimeOffset ToUtc(DateTime local)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeZoneInfo zone = Zone;
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(1);
        }

        TimeSpan offset = zone.IsAmbiguousTime(unspecified)
            ? zone.GetAmbiguousTimeOffsets(unspecified).Max()
            : zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static bool IsUtcName(string id) =>
        id is "UTC" or "Etc/UTC" or "Etc/UCT" or "UCT" or "Universal" or "Etc/Universal" or "Zulu" or "Etc/Zulu"
            or "GMT" or "Etc/GMT" or "Coordinated Universal Time";
}
