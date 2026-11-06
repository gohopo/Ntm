using System.Collections.Concurrent;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 一个冷却条目的键。密钥维度用的是密钥值的指纹而不是行 ID：
/// 同一份密钥配在多个 Channel 上时，上游限的是那把密钥，冷却必须一起生效（Q21）。
/// </summary>
public readonly record struct CooldownKey(
    CooldownScope Scope,
    string? KeyFingerprint,
    int ChannelId,
    string? UpstreamModel)
{
    public static CooldownKey ForKey(string fingerprint) =>
        new(CooldownScope.Key, fingerprint, 0, null);

    public static CooldownKey ForKeyModel(string fingerprint, string upstreamModel) =>
        new(CooldownScope.KeyModel, fingerprint, 0, upstreamModel);

    public static CooldownKey ForChannelModel(int channelId, string upstreamModel) =>
        new(CooldownScope.ChannelModel, null, channelId, upstreamModel);

    public static CooldownKey ForChannel(int channelId) =>
        new(CooldownScope.Channel, null, channelId, null);
}

/// <summary>一次获取尝试的结果。</summary>
public sealed class CooldownPermit
{
    internal CooldownPermit(IReadOnlyList<CooldownKey> probes) => Probes = probes;

    /// <summary>这次尝试顺带认领了哪些半开探测。成功或失败时要相应处理。</summary>
    internal IReadOnlyList<CooldownKey> Probes { get; }
}

/// <summary>被冷却挡住了。</summary>
public sealed class CooldownBlock
{
    public required CooldownScope Scope { get; init; }

    public required DateTimeOffset Until { get; init; }

    /// <summary>挡在密钥维度：换这个渠道的下一把密钥还有机会。否则整个候选都得跳过。</summary>
    public bool IsKeyScoped => Scope is CooldownScope.Key or CooldownScope.KeyModel;
}

/// <summary>
/// 进程内的冷却状态（ADR-0003：重启丢掉冷却是可以接受的）。
///
/// 四个作用域各自记一条阶梯，按（作用域, 错误类别）分开：401 紧跟在 60 秒的 429 后面时，
/// 应该按认证失败的 5 分钟起算，不是把 429 的 60 秒翻倍——ccLoad 的加倍忽略新的状态码，正是这个毛病。
/// 到期后先只放一个请求去探，成功才真正放开；探测期间其他请求仍按冷却处理。
/// </summary>
public sealed class CooldownRegistry(TimeProvider? timeProvider = null, Func<double>? random = null)
{
    /// <summary>探测的租约。拿到探测权的请求崩了或者客户端跑了，到期自动放开，不会永远卡住。</summary>
    private static readonly TimeSpan ProbeLease = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<CooldownKey, Entry> _entries = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>某个候选（渠道 + 模型）现在能不能用。只看渠道与渠道+模型两层，密钥在获取时才看。</summary>
    public CooldownBlock? CheckCandidate(int channelId, string upstreamModel)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return Blocked(CooldownKey.ForChannel(channelId), now)
            ?? Blocked(CooldownKey.ForChannelModel(channelId, upstreamModel), now);
    }

    /// <summary>某把密钥整个（不分模型）是否在冷却。ModelSync 挑密钥时用（Q136）：列模型不针对某个模型。</summary>
    public bool IsKeyCooling(string fingerprint) =>
        Blocked(CooldownKey.ForKey(fingerprint), _time.GetUtcNow()) is not null;

    /// <summary>某把密钥在这个模型上现在能不能用。</summary>
    public CooldownBlock? CheckKey(string fingerprint, string upstreamModel)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return Blocked(CooldownKey.ForKey(fingerprint), now)
            ?? Blocked(CooldownKey.ForKeyModel(fingerprint, upstreamModel), now);
    }

    /// <summary>
    /// 为一次尝试占位。四个作用域都没被挡住才给许可；其中已经到期的作用域由这次尝试去探，
    /// 探测权记在许可里。被挡住时返回 null 并给出 <paramref name="block"/>，已认领的探测全部放回。
    /// </summary>
    public CooldownPermit? TryAcquire(
        int channelId,
        string upstreamModel,
        string keyFingerprint,
        out CooldownBlock? block)
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<CooldownKey> probes = [];

        // 顺序是从粗到细：渠道级被挡住时，不必再去碰密钥级的探测权。
        CooldownKey[] scopes =
        [
            CooldownKey.ForChannel(channelId),
            CooldownKey.ForChannelModel(channelId, upstreamModel),
            CooldownKey.ForKey(keyFingerprint),
            CooldownKey.ForKeyModel(keyFingerprint, upstreamModel),
        ];

        foreach (CooldownKey scope in scopes)
        {
            if (!_entries.TryGetValue(scope, out Entry? entry))
            {
                continue;
            }

            lock (entry.Gate)
            {
                if (now < entry.Until)
                {
                    Release(probes);
                    block = new CooldownBlock { Scope = scope.Scope, Until = entry.Until };
                    return null;
                }

                if (entry.ProbeUntil is { } lease && now < lease)
                {
                    // 已经有人在探了。对其他请求来说这个作用域还在冷却。
                    Release(probes);
                    block = new CooldownBlock { Scope = scope.Scope, Until = lease };
                    return null;
                }

                entry.ProbeUntil = now + ProbeLease;
                probes.Add(scope);
            }
        }

        block = null;
        return new CooldownPermit(probes);
    }

    /// <summary>
    /// 报告一次尝试的结果。成功清掉四个作用域的冷却与阶梯——
    /// 在（渠道, 密钥, 模型）上成功一次，说明包着它的每一层此刻都是通的。
    /// 失败只冷却错误类别指向的那一层，其余作用域的探测权放回（它们并没有被证明过）。
    /// </summary>
    public DateTimeOffset? Complete(
        CooldownPermit permit,
        int channelId,
        string upstreamModel,
        string keyFingerprint,
        FailureClass failure,
        DateTimeOffset? upstreamResetAt)
    {
        ArgumentNullException.ThrowIfNull(permit);

        if (failure == FailureClass.None)
        {
            Clear(channelId, upstreamModel, keyFingerprint);
            return null;
        }

        CooldownScope scope = CooldownPolicy.ScopeOf(failure);
        if (scope == CooldownScope.None)
        {
            Release(permit.Probes);
            return null;
        }

        CooldownKey key = scope switch
        {
            CooldownScope.Key => CooldownKey.ForKey(keyFingerprint),
            CooldownScope.KeyModel => CooldownKey.ForKeyModel(keyFingerprint, upstreamModel),
            CooldownScope.ChannelModel => CooldownKey.ForChannelModel(channelId, upstreamModel),
            _ => CooldownKey.ForChannel(channelId),
        };

        Release(permit.Probes.Where(p => p != key).ToList());

        DateTimeOffset now = _time.GetUtcNow();
        Entry entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry.Gate)
        {
            // 同类失败的连续次数。换了错误类别就从这一类的初始值重新起算。
            int consecutive = 1;
            if (entry.LastFailure == failure
                && entry.LastFailureAt is { } last
                && now - last <= CooldownPolicy.LadderResetAfter)
            {
                consecutive = entry.Consecutive + 1;
            }

            entry.LastFailure = failure;
            entry.LastFailureAt = now;
            entry.Consecutive = consecutive;
            entry.ProbeUntil = null;

            // 上游给了明确的恢复时间就以它为准，不叠抖动也不翻倍（Q29）。
            entry.Until = upstreamResetAt is { } reset && reset > now
                ? reset
                : now + CooldownPolicy.Backoff(failure, consecutive, _random());

            return entry.Until;
        }
    }

    /// <summary>（渠道, 模型, 密钥）这条路成功了：四个作用域全部放开。</summary>
    public void Clear(int channelId, string upstreamModel, string keyFingerprint)
    {
        _entries.TryRemove(CooldownKey.ForChannel(channelId), out _);
        _entries.TryRemove(CooldownKey.ForChannelModel(channelId, upstreamModel), out _);
        _entries.TryRemove(CooldownKey.ForKey(keyFingerprint), out _);
        _entries.TryRemove(CooldownKey.ForKeyModel(keyFingerprint, upstreamModel), out _);
    }

    /// <summary>当前所有还在冷却中的条目。面板和测试用。</summary>
    /// <summary>
    /// Admin 手动解除一条冷却（Q262）：连同升级的连续失败次数和进行中的探测名额一起丢掉，下次失败从头算。
    /// 不碰健康度。返回这条冷却原来是否存在。
    /// </summary>
    public bool Remove(CooldownKey key) => _entries.TryRemove(key, out _);

    public IReadOnlyList<(CooldownKey Key, DateTimeOffset Until, FailureClass Failure)> Active()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<(CooldownKey, DateTimeOffset, FailureClass)> active = [];
        foreach ((CooldownKey key, Entry entry) in _entries)
        {
            lock (entry.Gate)
            {
                if (now < entry.Until)
                {
                    active.Add((key, entry.Until, entry.LastFailure));
                }
            }
        }

        return active;
    }

    private CooldownBlock? Blocked(CooldownKey key, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        lock (entry.Gate)
        {
            if (now < entry.Until)
            {
                return new CooldownBlock { Scope = key.Scope, Until = entry.Until };
            }

            if (entry.ProbeUntil is { } lease && now < lease)
            {
                return new CooldownBlock { Scope = key.Scope, Until = lease };
            }
        }

        return null;
    }

    private void Release(IReadOnlyList<CooldownKey> probes)
    {
        foreach (CooldownKey probe in probes)
        {
            if (_entries.TryGetValue(probe, out Entry? entry))
            {
                lock (entry.Gate)
                {
                    entry.ProbeUntil = null;
                }
            }
        }
    }

    private sealed class Entry
    {
        public Lock Gate { get; } = new();

        public DateTimeOffset Until { get; set; }

        /// <summary>有人正在半开探测，租约到这个时刻为止。</summary>
        public DateTimeOffset? ProbeUntil { get; set; }

        public FailureClass LastFailure { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public int Consecutive { get; set; }
    }
}
