using System.Net;
using System.Net.Sockets;

namespace Ntm.Server.Admin;

/// <summary>
/// 登录限流（Q353，ADR-0023）。只数失败的次数，登录成功不占名额：
/// <list type="bullet">
/// <item>每个来源每分钟最多失败 <see cref="FailuresPerAddress"/> 次。IPv4 按单个地址算，IPv6 按 /64 网段算——
/// 拿到一个 /64 的人手里有数不清的地址，按完整地址分桶等于没限。</item>
/// <item>整个网关每分钟最多失败 <see cref="FailuresGlobal"/> 次。超了这一分钟谁都登不进去：
/// 攻击者能让管理员一分钟内登不上，但猜不出密码。</item>
/// </list>
/// 窗口从这一桶的第一次失败算起，满一分钟清零。只在内存里记，重启清零。
/// </summary>
internal sealed class LoginThrottle(TimeProvider timeProvider) : IDisposable
{
    public const int FailuresPerAddress = 5;

    public const int FailuresGlobal = 30;

    /// <summary>桶的数量上限：超过就先清掉过期的，再不行整个清空（宁可放过几次，也不让内存被撑爆）。</summary>
    private const int MaxBuckets = 10_000;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private Bucket _global;
    private readonly SemaphoreSlim _passwordCheck = new(1, 1);

    /// <summary>校验与失败计数不能被并发请求穿透；单管理员的密码检查串行执行。</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _passwordCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CheckLease(_passwordCheck);
    }

    public void Dispose() => _passwordCheck.Dispose();

    private sealed class CheckLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }


    /// <summary>来源已经被挡住时返回还要等多久，否则 null。</summary>
    public TimeSpan? RetryAfter(IPAddress? address)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string key = PartitionKey(address);
        lock (_gate)
        {
            TimeSpan? wait = null;
            if (_global.IsFull(now, FailuresGlobal))
            {
                wait = _global.RemainingFrom(now);
            }

            if (_buckets.TryGetValue(key, out Bucket bucket) && bucket.IsFull(now, FailuresPerAddress))
            {
                TimeSpan local = bucket.RemainingFrom(now);
                wait = wait is null || local > wait ? local : wait;
            }

            return wait;
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string key = PartitionKey(address);
        lock (_gate)
        {
            _global = _global.Add(now);

            if (!_buckets.ContainsKey(key) && _buckets.Count >= MaxBuckets)
            {
                foreach (string stale in _buckets.Where(b => b.Value.IsExpired(now)).Select(b => b.Key).ToList())
                {
                    _buckets.Remove(stale);
                }

                if (_buckets.Count >= MaxBuckets)
                {
                    _buckets.Clear();
                }
            }

            _buckets[key] = _buckets.GetValueOrDefault(key).Add(now);
        }
    }

    /// <summary>分桶用的键：IPv4（含 IPv4 映射的 IPv6）是地址本身，IPv6 是前 64 位。</summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private readonly record struct Bucket(DateTimeOffset Start, int Count)
    {
        public bool IsExpired(DateTimeOffset now) => Count == 0 || now - Start >= Window;

        public bool IsFull(DateTimeOffset now, int limit) => !IsExpired(now) && Count >= limit;

        public TimeSpan RemainingFrom(DateTimeOffset now) => Start + Window - now;

        public Bucket Add(DateTimeOffset now) => IsExpired(now) ? new Bucket(now, 1) : this with { Count = Count + 1 };
    }
}
