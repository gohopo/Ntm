using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 缓存文件的形状（Q72）。只存被渠道声明的那几个供应商的单价，连 ETag 一起，
/// 这样重启后不用等下载就能计费。
/// </summary>
internal sealed class CachedCatalog
{
    /// <summary>当前的文件格式版本。2 加了思考输出单价（ADR-0013）。</summary>
    public const int CurrentSchema = 2;

    /// <summary>
    /// 文件格式版本，旧文件没有这个字段就是 0。低于当前版本时照样用它计费，但丢掉 ETag：
    /// 否则服务端回 304，新加的字段永远补不上。
    /// </summary>
    public int Schema { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? FetchedAt { get; set; }

    /// <summary>这次下载时「要求保留」的供应商。少了就说明有新渠道，需要重新完整下载。</summary>
    public List<string> RequestedProviders { get; set; } = [];

    public List<CachedProvider> Providers { get; set; } = [];

    public List<CachedPrice> Prices { get; set; } = [];
}

internal sealed class CachedProvider
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int ModelCount { get; set; }
}

internal sealed class CachedPrice
{
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public decimal? Input { get; set; }

    public decimal? CacheRead { get; set; }

    public decimal? CacheWrite5m { get; set; }

    public decimal? CacheWrite1h { get; set; }

    public decimal? Output { get; set; }

    public decimal? ThinkingOutput { get; set; }

    public List<CachedLongContextPrice>? LongContext { get; set; }
}

internal sealed class CachedLongContextPrice
{
    public long Over { get; set; }

    public decimal? Input { get; set; }

    public decimal? CacheRead { get; set; }

    public decimal? CacheWrite5m { get; set; }

    public decimal? CacheWrite1h { get; set; }

    public decimal? Output { get; set; }

    public decimal? ThinkingOutput { get; set; }
}

/// <summary>缓存文件的读写。文件损坏就当没有缓存，不让它挡住启动。</summary>
internal static class PriceCatalogCache
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<(PriceCatalog Catalog, IReadOnlySet<string> Requested)?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        CachedCatalog? cached = await JsonSerializer
            .DeserializeAsync<CachedCatalog>(stream, Json, cancellationToken)
            .ConfigureAwait(false);

        if (cached is null)
        {
            return null;
        }

        var catalog = new PriceCatalog(
            cached.Providers.Select(p => new CatalogProvider(p.Id, p.Name, p.ModelCount)),
            cached.Prices.Select(ToPrice),
            cached.Schema < CachedCatalog.CurrentSchema ? null : cached.ETag,
            cached.FetchedAt);

        return (catalog, cached.RequestedProviders.ToHashSet(StringComparer.Ordinal));
    }

    public static async Task WriteAsync(
        string path,
        PriceCatalog catalog,
        IReadOnlySet<string> requested,
        CancellationToken cancellationToken)
    {
        var cached = new CachedCatalog
        {
            Schema = CachedCatalog.CurrentSchema,
            ETag = catalog.ETag,
            FetchedAt = catalog.FetchedAt,
            RequestedProviders = [.. requested.Order(StringComparer.Ordinal)],
            Providers = [.. catalog.Providers.Select(p => new CachedProvider
            {
                Id = p.Id,
                Name = p.Name,
                ModelCount = p.ModelCount,
            })],
            Prices = [.. catalog.PricedProviders
                .SelectMany(catalog.PricesOf)
                .OrderBy(p => p.Provider, StringComparer.Ordinal)
                .ThenBy(p => p.ModelId, StringComparer.Ordinal)
                .Select(ToCached)],
        };

        // 先写临时文件再改名：断电或崩溃时不会留下半份文件。
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, cached, Json, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static CatalogPrice ToPrice(CachedPrice cached) => new(
        cached.Provider,
        cached.Model,
        new PriceSet(cached.Input, cached.CacheRead, cached.CacheWrite5m, cached.CacheWrite1h, cached.Output, cached.ThinkingOutput),
        cached.LongContext is null or { Count: 0 }
            ? CatalogPrice.NoLongContext
            : [.. cached.LongContext.Select(tier => new LongContextPrice(
                tier.Over,
                new PriceSet(tier.Input, tier.CacheRead, tier.CacheWrite5m, tier.CacheWrite1h, tier.Output, tier.ThinkingOutput)))]);

    private static CachedPrice ToCached(CatalogPrice price) => new()
    {
        Provider = price.Provider,
        Model = price.ModelId,
        Input = price.Prices.Input,
        CacheRead = price.Prices.CacheRead,
        CacheWrite5m = price.Prices.CacheWrite5m,
        CacheWrite1h = price.Prices.CacheWrite1h,
        Output = price.Prices.Output,
        ThinkingOutput = price.Prices.ThinkingOutput,
        LongContext = price.LongContext.Count == 0
            ? null
            : [.. price.LongContext.Select(tier => new CachedLongContextPrice
            {
                Over = tier.InputTokensOver,
                Input = tier.Prices.Input,
                CacheRead = tier.Prices.CacheRead,
                CacheWrite5m = tier.Prices.CacheWrite5m,
                CacheWrite1h = tier.Prices.CacheWrite1h,
                Output = tier.Prices.Output,
                ThinkingOutput = tier.Prices.ThinkingOutput,
            })],
    };
}
