using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ntm.Gateway.Data;

/// <summary>
/// 时间戳按 Unix 毫秒整数存（ADR-0012）。EF 的 SQLite 提供程序把 DateTimeOffset 存成带偏移量的文本，
/// 字符串比较在偏移量不同时就不是时间顺序，而请求记录必须能在 SQL 里排序和按区间过滤。
/// </summary>
public sealed class UnixMillisecondsConverter : ValueConverter<DateTimeOffset, long>
{
    public UnixMillisecondsConverter()
        : base(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value))
    {
    }
}

/// <summary>可空版本。</summary>
public sealed class NullableUnixMillisecondsConverter : ValueConverter<DateTimeOffset?, long?>
{
    public NullableUnixMillisecondsConverter()
        : base(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null)
    {
    }
}
