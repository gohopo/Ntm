using System.Collections.Frozen;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 一份价目目录快照：供应商清单，加上被渠道声明的那几个供应商的单价（Q72 只留需要的部分）。
/// 不可变，整份替换。
/// </summary>
public sealed class PriceCatalog
{
    /// <summary>还没取到目录时用这个：清单和单价都是空的。</summary>
    public static readonly PriceCatalog Empty = new([], [], null, null);

    private readonly FrozenDictionary<string, FrozenDictionary<string, CatalogPrice>> _prices;

    public PriceCatalog(
        IEnumerable<CatalogProvider> providers,
        IEnumerable<CatalogPrice> prices,
        string? etag,
        DateTimeOffset? fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        Providers = [.. providers.OrderBy(p => p.Id, StringComparer.Ordinal)];
        _prices = prices
            .GroupBy(p => p.Provider, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.ToFrozenDictionary(p => p.ModelId, StringComparer.Ordinal),
                StringComparer.Ordinal);
        ETag = etag;
        FetchedAt = fetchedAt;
    }

    /// <summary>目录里所有供应商，按 id 排序。渠道编辑页的下拉列表用它。</summary>
    public IReadOnlyList<CatalogProvider> Providers { get; }

    /// <summary>这份目录里带了单价的供应商 id。</summary>
    public IReadOnlyCollection<string> PricedProviders => _prices.Keys;

    /// <summary>上次成功下载时服务端给的 ETag，下次带 If-None-Match 用。</summary>
    public string? ETag { get; }

    /// <summary>上次成功下载的时间。</summary>
    public DateTimeOffset? FetchedAt { get; }

    /// <summary>一个单价都没有。</summary>
    public bool IsEmpty => _prices.Count == 0;

    /// <summary>某个供应商下的所有条目；没有这个供应商就是空的。</summary>
    public IReadOnlyCollection<CatalogPrice> PricesOf(string provider) =>
        _prices.TryGetValue(provider ?? "", out FrozenDictionary<string, CatalogPrice>? models)
            ? models.Values
            : [];

    /// <summary>按 (Provider, 模型 id) 直接取条目。</summary>
    public CatalogPrice? Find(string provider, string modelId) =>
        _prices.TryGetValue(provider ?? "", out FrozenDictionary<string, CatalogPrice>? models)
            && models.TryGetValue(modelId ?? "", out CatalogPrice? price)
                ? price
                : null;

    /// <summary>这几个供应商是不是都已经在这份目录里（缺了就要重新完整下载，Q72）。</summary>
    public bool Covers(IEnumerable<string> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return providers.All(provider => string.IsNullOrEmpty(provider) || _prices.ContainsKey(provider));
    }
}
