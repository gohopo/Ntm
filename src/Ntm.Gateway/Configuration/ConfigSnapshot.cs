using System.Collections.Frozen;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Routing;
using Ntm.Protocol;

namespace Ntm.Gateway.Configuration;

/// <summary>选路要用到的一个渠道，密钥已解密。</summary>
public sealed class ChannelSnapshot
{
    public required int ChannelId { get; init; }

    public required string ChannelName { get; init; }

    public required string BaseUrl { get; init; }

    public string? Path { get; init; }

    public required ApiFormat ApiFormat { get; init; }

    /// <summary>Anthropic 渠道用 Authorization: Bearer 带密钥，而不是 x-api-key（Q166）。</summary>
    public bool AnthropicBearerAuth { get; init; }

    /// <summary>OpenAI 入站转到这个 Anthropic 渠道时，客户端没写断点就由网关加缓存断点（Q247）。</summary>
    public bool AutoCacheBreakpoints { get; init; }

    /// <summary>密钥属于同一个账号：Responses 落点只钉到渠道，不钉到密钥（Q306）。</summary>
    public bool KeysShareAccount { get; init; }

    /// <summary>推理强度转到这个 Anthropic 渠道时思考参数的写法（ADR-0021）。</summary>
    public Ntm.Protocol.Anthropic.AnthropicThinkingStyle ThinkingStyle { get; init; }

    /// <summary>价目目录里的供应商 id，小写。空表示没有自动单价（Q71）。</summary>
    public string Provider { get; init; } = "";

    public int Priority { get; init; }

    public int Weight { get; init; } = 1;

    public int MultiplierBasisPoints { get; init; } = 10000;

    public required IReadOnlyList<ChannelKeySnapshot> Keys { get; init; }

    /// <summary>这个渠道上游提供的模型名。MatchRule 只在这份清单里匹配。</summary>
    public IReadOnlyList<string> UpstreamModels { get; init; } = [];

    /// <summary>每日费用上限，纳美元；null 表示不限（Q77）。</summary>
    public long? DailyCostLimitNanoUsd { get; init; }

    /// <summary>可用时段；空表示随时可用（Q81）。</summary>
    public IReadOnlyList<Restrictions.TimeWindow> TimeWindows { get; init; } = [];

    /// <summary>出站代理（Q130）。null 表示直连。</summary>
    public Upstream.ProxyAddress? Proxy { get; init; }

    /// <summary>这个渠道的请求改写规则，已解析好（Q144）。没有规则时是 <see cref="Rewrite.RequestRewriter.Empty"/>。</summary>
    public Rewrite.RequestRewriter Rewriter { get; init; } = Rewrite.RequestRewriter.Empty;

    /// <summary>这个渠道的 DebugCapture 到期时刻；null 或已过去都表示关（Q155）。</summary>
    public DateTimeOffset? DebugCaptureUntil { get; init; }
}

/// <summary>一把可用的密钥，密钥值已解密。</summary>
public sealed class ChannelKeySnapshot
{
    public required int ChannelKeyId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// 密钥原文。只在内存里（ADR-0003 的运行时状态同理）：热路径上不做解密，
    /// 而且进程本来就持有密钥环，能解开库里任何一把。它从不进日志。
    /// </summary>
    public required string Secret { get; init; }

    public required string Fingerprint { get; init; }

    public required string Prefix { get; init; }

    public int Priority { get; init; }

    /// <summary>每分钟最多几次 Attempt；null 表示不限（Q79）。</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>同时最多几个 Attempt；null 表示不限（Q80）。</summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>能打的 UpstreamModel；空表示全部（Q82）。</summary>
    public FrozenSet<string> AllowedModels { get; init; } = FrozenSet<string>.Empty;

    public bool Allows(string upstreamModel) => AllowedModels.Count == 0 || AllowedModels.Contains(upstreamModel);
}

/// <summary>
/// 一个候选：一个 Channel 加它上面的一个 UpstreamModel。选路排的是它，
/// 一次 Attempt 是用某个 ChannelKey 去打它。
/// </summary>
public sealed class RouteCandidate
{
    public required ChannelSnapshot Channel { get; init; }

    /// <summary>写进上游请求里的模型名。</summary>
    public required string UpstreamModel { get; init; }

    public required int Priority { get; init; }

    /// <summary>人配置的权重。健康度在这个基础上按比例下调（Q38）。</summary>
    public required int Weight { get; init; }

    /// <summary>产生这个候选的匹配规则序号（从 1 开始）。只给管理界面的预览用，选路不看它。</summary>
    public int RuleOrdinal { get; init; }

    public int ChannelId => Channel.ChannelId;
}

/// <summary>同一个 Priority 档里的候选。档与档之间不混流。</summary>
public sealed class PriorityGroup
{
    public required int Priority { get; init; }

    public required IReadOnlyList<RouteCandidate> Candidates { get; init; }

    /// <summary>
    /// 这一档的平滑加权轮询状态。它随快照一起重建：配置变更不频繁，
    /// 而平滑加权轮询几轮就收敛回来，不值得为了保留几个计数器额外维护跨快照的状态。
    /// </summary>
    public SmoothWeightedRoundRobin RoundRobin { get; } = new();
}

/// <summary>
/// 一个下发模型的路由：匹配规则已经展开成候选，并按 Priority 分好档。
/// 匹配在构建快照时做一次，不在热路径上跑正则。
/// </summary>
public sealed class PublishedModelRoute
{
    public required int PublishedModelId { get; init; }

    public required string Name { get; init; }

    public string? DisplayName { get; init; }

    public required RequestKind RequestKind { get; init; }

    public required bool Enabled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>按 Priority 降序的档。</summary>
    public required IReadOnlyList<PriorityGroup> PriorityGroups { get; init; }

    /// <summary>规则里的毛病（非法正则等）。面板要显示出来。</summary>
    public IReadOnlyList<RuleProblem> Problems { get; init; } = [];

    public int CandidateCount => PriorityGroups.Sum(t => t.Candidates.Count);
}

/// <summary>
/// 一份不可变的配置快照。配置读取只走它，写入时整份重建再换上（m00097 声明的默认做法），
/// 不维护一堆可变的按实体缓存——octopus 那样做就得在每处写入手工失效多个缓存。
/// </summary>
public sealed class ConfigSnapshot
{
    public static readonly ConfigSnapshot Empty = new(
        new Dictionary<string, ApiKeySnapshot>(), [], []);

    public ConfigSnapshot(
        IReadOnlyDictionary<string, ApiKeySnapshot> tokensByFingerprint,
        IReadOnlyList<ChannelSnapshot> channels,
        IReadOnlyList<PublishedModelRoute> routes,
        IReadOnlyDictionary<(string Provider, string UpstreamModel), ModelPrice>? prices = null)
    {
        ArgumentNullException.ThrowIfNull(tokensByFingerprint);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(routes);

        Prices = (prices ?? new Dictionary<(string, string), ModelPrice>()).ToFrozenDictionary();
        ApiKeysByFingerprint = tokensByFingerprint.ToFrozenDictionary(StringComparer.Ordinal);
        Channels = [.. channels];
        Routes = [.. routes];
        RoutesByName = routes
            .Where(r => r.Enabled)
            .ToFrozenDictionary(r => r.Name, r => r, StringComparer.Ordinal);
    }

    /// <summary>全局 DebugCapture 的到期时刻；null 或已过去都表示关（Q154）。</summary>
    public DateTimeOffset? DebugCaptureUntil { get; init; }

    public FrozenDictionary<string, ApiKeySnapshot> ApiKeysByFingerprint { get; }

    public IReadOnlyList<ChannelSnapshot> Channels { get; }

    /// <summary>所有下发模型，含未启用的。面板用。</summary>
    public IReadOnlyList<PublishedModelRoute> Routes { get; }

    /// <summary>只含已启用的，按名字查。客户端请求走它。</summary>
    public FrozenDictionary<string, PublishedModelRoute> RoutesByName { get; }

    /// <summary>单价，按 (Provider, UpstreamModel) 查，两段都是小写（Q48/Q71）。</summary>
    public FrozenDictionary<(string Provider, string UpstreamModel), ModelPrice> Prices { get; }

    /// <summary>找这个渠道这个上游模型的单价；没有行或行是空的都返回 null。</summary>
    public ModelPrice? FindPrice(string? provider, string? upstreamModel)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(upstreamModel))
        {
            return null;
        }

        return Prices.TryGetValue(
            (provider.ToLowerInvariant(), upstreamModel.ToLowerInvariant()),
            out ModelPrice? price)
            ? price
            : null;
    }

    /// <summary>按指纹找 API 密钥（ADR-0006：库里没有原文）。</summary>
    public ApiKeySnapshot? FindApiKey(string fingerprint) =>
        ApiKeysByFingerprint.TryGetValue(fingerprint, out ApiKeySnapshot? token) ? token : null;

    /// <summary>按名字找已启用的下发模型；没有就是没下发。</summary>
    public PublishedModelRoute? FindRoute(string name) =>
        RoutesByName.TryGetValue(name, out PublishedModelRoute? route) ? route : null;
}

/// <summary>一个可用的 API 密钥。</summary>
public sealed class ApiKeySnapshot
{
    public required int ApiKeyId { get; init; }

    public required string Name { get; init; }

    /// <summary>这个 API 密钥的 DebugCapture 到期时刻；null 或已过去都表示关（Q155）。</summary>
    public DateTimeOffset? DebugCaptureUntil { get; init; }

    public required string Fingerprint { get; init; }

    public long? CostLimitNanoUsd { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    /// <summary>同时最多几个 Request；null 表示不限（Q80）。</summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>能用的 PublishedModel；空表示全部（Q83）。</summary>
    public FrozenSet<string> AllowedModels { get; init; } = FrozenSet<string>.Empty;

    /// <summary>只走这些渠道；空表示不限。</summary>
    public FrozenSet<int> AllowedChannelIds { get; init; } = FrozenSet<int>.Empty;

    public FrozenSet<int> BlockedChannelIds { get; init; } = FrozenSet<int>.Empty;

    public bool AllowsModel(string publishedModel) => AllowedModels.Count == 0 || AllowedModels.Contains(publishedModel);

    /// <summary>先按白名单过滤，再减去黑名单（Q83）。</summary>
    public bool AllowsChannel(int channelId) =>
        (AllowedChannelIds.Count == 0 || AllowedChannelIds.Contains(channelId)) && !BlockedChannelIds.Contains(channelId);
}
