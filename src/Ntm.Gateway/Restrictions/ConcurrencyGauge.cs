using System.Collections.Concurrent;

namespace Ntm.Gateway.Restrictions;

/// <summary>
/// 进行中的数量，按键计（Q80）。占位拿到一个租约，<see cref="IDisposable.Dispose"/> 时归还——
/// 调用方放在 using / finally 里，流结束、客户端断开、被中断都一样会还。不设上限的也计数，面板要看。
/// </summary>
public sealed class ConcurrencyGauge<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Counter> _counters;

    public ConcurrencyGauge(IEqualityComparer<TKey>? comparer = null)
    {
        _counters = new ConcurrentDictionary<TKey, Counter>(comparer);
    }

    public int Current(TKey key) => _counters.TryGetValue(key, out Counter? counter) ? counter.Value : 0;

    public bool HasRoom(TKey key, int? limit) => limit is not { } max || Current(key) < max;

    /// <summary>占一个位置。满了返回 null。</summary>
    public IDisposable? TryAcquire(TKey key, int? limit)
    {
        Counter counter = _counters.GetOrAdd(key, _ => new Counter());
        return counter.TryIncrement(limit) ? new Lease(counter) : null;
    }

    private sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public bool TryIncrement(int? limit)
        {
            while (true)
            {
                int current = Volatile.Read(ref _value);
                if (limit is { } max && current >= max)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _value, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void Decrement() => Interlocked.Decrement(ref _value);
    }

    private sealed class Lease(Counter counter) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                counter.Decrement();
            }
        }
    }
}

/// <summary>两种并发计数：渠道密钥按指纹、一次 Attempt 占一个位置；API 密钥按 ID、一个 Request 从头到尾占一个位置（Q80）。</summary>
public sealed class ConcurrencyGauges
{
    public ConcurrencyGauge<string> ChannelKeys { get; } = new(StringComparer.Ordinal);

    public ConcurrencyGauge<int> ApiKeys { get; } = new();
}
