using System.Collections.Concurrent;

namespace Ntm.Gateway.Routing;

/// <summary>记住的落点：渠道、它上面的 UpstreamModel、渠道密钥（按密钥值指纹，Q119）。</summary>
public sealed record AffinityPin(int ChannelId, string UpstreamModel, string KeyFingerprint);

/// <summary>
/// Conversation → 上次落点（Q19/Q121）。只在内存里（ADR-0003）：重启后每个 Conversation 重新选一次，
/// 代价是一次提示缓存没命中。空闲 1 小时过期，命中时续期；超过上限先清过期的，再清最久没用的。
/// </summary>
public sealed class AffinityTable(TimeProvider timeProvider, TimeSpan? idleExpiry = null, int maxEntries = AffinityTable.DefaultMaxEntries)
{
    public const int DefaultMaxEntries = 100_000;

    public static readonly TimeSpan DefaultIdleExpiry = TimeSpan.FromHours(1);

    private readonly TimeSpan _idleExpiry = idleExpiry ?? DefaultIdleExpiry;
    private readonly ConcurrentDictionary<UInt128, Entry> _entries = new();
    private readonly Lock _trimGate = new();

    public int Count => _entries.Count;

    public AffinityPin? Find(UInt128 conversation)
    {
        if (!_entries.TryGetValue(conversation, out Entry? entry))
        {
            return null;
        }

        if (timeProvider.GetUtcNow() - entry.LastUsed >= _idleExpiry)
        {
            _entries.TryRemove(new KeyValuePair<UInt128, Entry>(conversation, entry));
            return null;
        }

        return entry.Pin;
    }

    /// <summary>这次落点成功了：记住它，并刷新空闲时间。</summary>
    public void Remember(UInt128 conversation, AffinityPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        _entries[conversation] = new Entry(pin, timeProvider.GetUtcNow());
        if (_entries.Count > maxEntries)
        {
            Trim();
        }
    }

    /// <summary>清到上限的 90%，免得每条新记录都触发一次全表扫描。</summary>
    private void Trim()
    {
        lock (_trimGate)
        {
            if (_entries.Count <= maxEntries)
            {
                return;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (KeyValuePair<UInt128, Entry> pair in _entries)
            {
                if (now - pair.Value.LastUsed >= _idleExpiry)
                {
                    _entries.TryRemove(pair);
                }
            }

            int target = maxEntries - (maxEntries / 10);
            int excess = _entries.Count - target;
            if (excess <= 0)
            {
                return;
            }

            foreach (KeyValuePair<UInt128, Entry> pair in _entries.OrderBy(p => p.Value.LastUsed).Take(excess).ToList())
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private sealed record Entry(AffinityPin Pin, DateTimeOffset LastUsed);
}
