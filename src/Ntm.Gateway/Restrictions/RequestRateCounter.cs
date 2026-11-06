using System.Collections.Concurrent;

namespace Ntm.Gateway.Restrictions;

/// <summary>
/// 每把渠道密钥（按指纹，Q21）每分钟发出的 Attempt 数，固定分钟桶（Q79）。在内存里，重启清零。
/// 只拦本网关发出去的量：同一把密钥在别处用掉的配额它不知道，那部分仍靠上游的 429 和冷却。
/// </summary>
public sealed class RequestRateCounter(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>这一分钟已经发了几次。</summary>
    public int CountThisMinute(string fingerprint) =>
        _buckets.TryGetValue(fingerprint, out Bucket? bucket) ? bucket.Count(CurrentMinute()) : 0;

    /// <summary>这一分钟还有没有余量。</summary>
    public bool HasRoom(string fingerprint, int? limit) => limit is not { } max || CountThisMinute(fingerprint) < max;

    /// <summary>占一次。满了返回 false，什么也不改。</summary>
    public bool TryTake(string fingerprint, int? limit)
    {
        Bucket bucket = _buckets.GetOrAdd(fingerprint, _ => new Bucket());
        return bucket.TryTake(CurrentMinute(), limit);
    }

    /// <summary>占了之后没发出去：退回这一次。只在同一分钟里有效，跨了分钟没什么可退的。</summary>
    public void Refund(string fingerprint)
    {
        if (_buckets.TryGetValue(fingerprint, out Bucket? bucket))
        {
            bucket.Refund(CurrentMinute());
        }
    }

    /// <summary>下一分钟开始的时刻：RPM 满了从这时起恢复。</summary>
    public DateTimeOffset NextMinute()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(1);
    }

    private long CurrentMinute() => timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;

    private sealed class Bucket
    {
        private readonly Lock _gate = new();
        private long _minute;
        private int _count;

        public int Count(long minute)
        {
            lock (_gate)
            {
                return _minute == minute ? _count : 0;
            }
        }

        public bool TryTake(long minute, int? limit)
        {
            lock (_gate)
            {
                if (_minute != minute)
                {
                    _minute = minute;
                    _count = 0;
                }

                if (limit is { } max && _count >= max)
                {
                    return false;
                }

                _count++;
                return true;
            }
        }

        public void Refund(long minute)
        {
            lock (_gate)
            {
                if (_minute == minute && _count > 0)
                {
                    _count--;
                }
            }
        }
    }
}
