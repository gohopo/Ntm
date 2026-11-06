using System.Globalization;
using System.Net.Http.Headers;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 从上游的响应头里读出「什么时候可以再试」（Q29）。有明确的重置时间就以它为准，
/// 比我们自己的指数退避准得多：ccLoad 从来没把 <c>Retry-After</c> 当成时长用，
/// 于是 60 秒的限流窗口要么等太久，要么太早撞上去。
///
/// 读取顺序：<c>retry-after-ms</c>（最精确）→ <c>Retry-After</c>（秒数或 HTTP 日期，跨厂商通用）
/// → <c>x-ratelimit-reset-requests</c> / <c>-tokens</c>（OpenAI 的时长串，如 <c>6m0s</c>、<c>1.5s</c>）
/// → <c>anthropic-ratelimit-*-reset</c>（RFC 3339 时刻）。
/// </summary>
internal static class UpstreamResetParser
{
    /// <summary>重置时间至少比现在晚这么多，避免上游报 0 导致我们立刻再撞一次。</summary>
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    /// <summary>最多信 24 小时。按日配额的上游会给出很远的重置时间，但再远也不该让候选消失一整天。</summary>
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(24);

    public static DateTimeOffset? Parse(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (TryMilliseconds(headers, "retry-after-ms", out TimeSpan afterMs))
        {
            return Clamp(now + afterMs, now);
        }

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return Clamp(now + delta, now);
            }

            if (retryAfter.Date is { } date)
            {
                return Clamp(date, now);
            }
        }

        // 两个维度（请求数、令牌数）谁耗尽了就用谁的重置时间；都不知道就取较晚的那个。
        DateTimeOffset? requests = RateLimitReset(headers, "requests", now);
        DateTimeOffset? tokens = RateLimitReset(headers, "tokens", now);
        return (requests, tokens) switch
        {
            (null, null) => null,
            (not null, null) => requests,
            (null, not null) => tokens,
            _ => requests >= tokens ? requests : tokens,
        };
    }

    private static DateTimeOffset? RateLimitReset(HttpResponseHeaders headers, string dimension, DateTimeOffset now)
    {
        // 还有余量的维度不是这次失败的原因，别拿它的重置时间当冷却时长。
        if (TryFirst(headers, "x-ratelimit-remaining-" + dimension, out string? remaining)
            && long.TryParse(remaining, CultureInfo.InvariantCulture, out long left)
            && left > 0)
        {
            return null;
        }

        if (TryFirst(headers, "x-ratelimit-reset-" + dimension, out string? reset)
            && TryDuration(reset!, out TimeSpan after))
        {
            return Clamp(now + after, now);
        }

        if (TryFirst(headers, "anthropic-ratelimit-" + dimension + "-reset", out string? instant)
            && DateTimeOffset.TryParse(
                instant,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset at))
        {
            return Clamp(at, now);
        }

        return null;
    }

    private static DateTimeOffset Clamp(DateTimeOffset at, DateTimeOffset now)
    {
        if (at < now + MinimumDelay)
        {
            return now + MinimumDelay;
        }

        return at > now + MaximumDelay ? now + MaximumDelay : at;
    }

    private static bool TryMilliseconds(HttpResponseHeaders headers, string name, out TimeSpan value)
    {
        if (TryFirst(headers, name, out string? raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
            && ms >= 0)
        {
            value = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// OpenAI 的时长串：<c>6m0s</c>、<c>1.5s</c>、<c>120ms</c>、<c>1h30m</c>。
    /// 不用正则，手写扫描：数字后面紧跟单位，逐段相加。
    /// </summary>
    internal static bool TryDuration(string text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // 没有单位的纯数字按秒算（有些上游就这么发）。
        if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
        {
            value = TimeSpan.FromSeconds(plain);
            return plain >= 0;
        }

        double total = 0;
        int i = 0;
        bool any = false;

        while (i < span.Length)
        {
            int numberStart = i;
            while (i < span.Length && (char.IsAsciiDigit(span[i]) || span[i] == '.'))
            {
                i++;
            }

            if (i == numberStart)
            {
                return false;
            }

            if (!double.TryParse(
                    span[numberStart..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return false;
            }

            int unitStart = i;
            while (i < span.Length && char.IsAsciiLetter(span[i]))
            {
                i++;
            }

            ReadOnlySpan<char> unit = span[unitStart..i];
            double seconds = unit switch
            {
                "ms" => number / 1000,
                "s" => number,
                "m" => number * 60,
                "h" => number * 3600,
                "d" => number * 86400,
                _ => double.NaN,
            };

            if (double.IsNaN(seconds))
            {
                return false;
            }

            total += seconds;
            any = true;
        }

        if (!any)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(total);
        return true;
    }

    private static bool TryFirst(HttpResponseHeaders headers, string name, out string? value)
    {
        if (headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            foreach (string candidate in values)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    value = candidate.Trim();
                    return true;
                }
            }
        }

        value = null;
        return false;
    }
}
