using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Routing;

/// <summary>写给 <see cref="ResponseLandingWriter"/> 的一条改动：<paramref name="Landing"/> 为 null 表示删掉。</summary>
public readonly record struct ResponseLandingWrite(string Id, ResponseLanding? Landing);

/// <summary>
/// ResponseLanding 表（ADR-0016、ADR-0020）。内存里是缓存，ntm-traffic.db 的 response_landings 是本账：
/// 没命中先查库，重启后链不再断（Q269）。写库走后台队列，请求路径不等磁盘；
/// 刚记下、还没落盘的落点在内存里，紧接着来的下一轮照样找得到。
/// 只认同一个 API 密钥（Q271）；过了保留期（按最后使用算，默认 30 天，Q285）就当没见过。
/// </summary>
public sealed class ResponseLandingTable(
    TimeProvider timeProvider,
    IServiceScopeFactory? scopes = null,
    TimeSpan? retention = null,
    int maxEntries = ResponseLandingTable.DefaultMaxEntries)
{
    /// <summary>内存里最多缓存这么多条；超了先丢最久没用的，库里的还在。</summary>
    public const int DefaultMaxEntries = 100_000;

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>续期最多每小时落一次盘：保留期按天算，不值得每次读都写库。</summary>
    private static readonly TimeSpan TouchPersistInterval = TimeSpan.FromHours(1);

    private readonly TimeSpan _retention = retention ?? DefaultRetention;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // 删了但还没落盘的 id：防止没命中时从库里把它又读回来。
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _settling = new(StringComparer.Ordinal);
    private readonly Channel<ResponseLandingWrite> _writes = Channel.CreateUnbounded<ResponseLandingWrite>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _trimGate = new();

    /// <summary>内存里缓存了多少条。</summary>
    public int Count => _entries.Count;

    public ChannelReader<ResponseLandingWrite> Writes => _writes.Reader;

    /// <summary>
    /// 找这个 id 的落点。种类不对、不是这个 API 密钥产出的、过了保留期，都返回 null，
    /// 和没见过一样（Q271/Q272）。找到了就续期。
    /// </summary>
    public async ValueTask<ResponseLanding?> FindAsync(
        string id,
        ResponseLandingKind kind,
        int apiKeyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0 || id.Length > ResponseLanding.MaxIdLength || _deleted.ContainsKey(id))
        {
            return null;
        }

        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            ResponseLanding? stored = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (stored is null || _deleted.ContainsKey(id))
            {
                return null;
            }

            entry = _entries.GetOrAdd(id, new Entry(stored, stored.LastUsedAt));
            TrimIfNeeded();
        }

        ResponseLanding landing = entry.Landing;
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (landing.Kind != kind || landing.ApiKeyId != apiKeyId || now - landing.LastUsedAt >= _retention)
        {
            return null;
        }

        ResponseLanding touched = landing.Touched(now);
        bool persist = now - entry.PersistedLastUsed >= TouchPersistInterval;
        if (_entries.TryUpdate(id, new Entry(touched, persist ? now : entry.PersistedLastUsed), entry) && persist)
        {
            Enqueue(new ResponseLandingWrite(id, touched));
        }

        return touched;
    }

    /// <summary>一次上游调用产出了这个 id：记下落点。</summary>
    public void Remember(ResponseLanding landing)
    {
        ArgumentNullException.ThrowIfNull(landing);
        if (landing.Id.Length == 0 || landing.Id.Length > ResponseLanding.MaxIdLength)
        {
            return;
        }

        _deleted.TryRemove(landing.Id, out _);
        _entries[landing.Id] = new Entry(landing, landing.LastUsedAt);
        Enqueue(new ResponseLandingWrite(landing.Id, landing));
        TrimIfNeeded();
    }

    /// <summary>上游确认删掉了这个响应：落点一起删（Q268 补充）。</summary>
    public void Forget(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _deleted[id] = 0;
        _entries.TryRemove(id, out _);
        Enqueue(new ResponseLandingWrite(id, null));
    }

    /// <summary>
    /// 占住回填权：同一个 background 响应同时被查两次时只有一次去回填（Q277）。
    /// 拿到了必须调 <see cref="EndSettle"/>。
    /// </summary>
    public bool TryBeginSettle(string id) => _settling.TryAdd(id, 0);

    /// <summary>回填结束。<paramref name="settled"/> 为 true 时以后不再回填。</summary>
    public void EndSettle(ResponseLanding landing, bool settled)
    {
        ArgumentNullException.ThrowIfNull(landing);
        if (settled && _entries.TryGetValue(landing.Id, out Entry? entry))
        {
            ResponseLanding done = entry.Landing.Settled();
            _entries[landing.Id] = entry with { Landing = done };
            Enqueue(new ResponseLandingWrite(landing.Id, done));
        }

        _settling.TryRemove(landing.Id, out _);
    }

    /// <summary>
    /// 后台轮询从库里读到的落点：内存里没有就放进去（不续期），有就用内存里那份，删过的返回 null（Q305）。
    /// </summary>
    internal ResponseLanding? Prime(ResponseLanding stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (_deleted.ContainsKey(stored.Id))
        {
            return null;
        }

        Entry entry = _entries.GetOrAdd(stored.Id, new Entry(stored, stored.LastUsedAt));
        TrimIfNeeded();
        return entry.Landing;
    }

    /// <summary>不再等它的用量（轮询放弃）：清掉标记并落盘。正有人在回填时不动，返回 false。</summary>
    internal bool GiveUpSettle(ResponseLanding landing)
    {
        ArgumentNullException.ThrowIfNull(landing);
        if (!TryBeginSettle(landing.Id))
        {
            return false;
        }

        EndSettle(landing, settled: true);
        return true;
    }

    /// <summary>写入器把删除落盘了：库里也没有了，不再需要拦着。</summary>
    internal void DeletePersisted(string id)
    {
        if (!_entries.ContainsKey(id))
        {
            _deleted.TryRemove(id, out _);
        }
    }

    internal void CompleteWrites() => _writes.Writer.TryComplete();

    private void Enqueue(ResponseLandingWrite write)
    {
        if (scopes is not null)
        {
            _writes.Writer.TryWrite(write);
        }
    }

    private async Task<ResponseLanding?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        if (scopes is null)
        {
            return null;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        return await db.ResponseLandings
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count <= maxEntries)
        {
            return;
        }

        // 清到上限的 90%，免得每条新记录都触发一次全表扫描。只清缓存，库里的还在。
        lock (_trimGate)
        {
            int excess = _entries.Count - (maxEntries - (maxEntries / 10));
            if (excess <= 0)
            {
                return;
            }

            foreach (KeyValuePair<string, Entry> pair in _entries
                .OrderBy(p => p.Value.Landing.LastUsedAt)
                .Take(excess)
                .ToList())
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private sealed record Entry(ResponseLanding Landing, DateTimeOffset PersistedLastUsed);
}
