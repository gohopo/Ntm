namespace Ntm.Gateway.Pricing;

/// <summary>一行单价是怎么来的（Q48）。</summary>
public enum PriceSource
{
    /// <summary>从价目目录自动配上的。刷新时会跟着目录更新。</summary>
    Auto = 1,

    /// <summary>人工填过的。刷新一律不动它（Q69）。</summary>
    Manual = 2,
}

/// <summary>
/// 一组长上下文单价（Q66），挂在 <see cref="PriceRow"/> 下，存成 JSON 列。
/// </summary>
public sealed class LongContextPriceEntry
{
    /// <summary>门槛：总输入 Token 超过这个数时，整个请求按这一组单价算。</summary>
    public long InputTokensOver { get; set; }

    public decimal? InputPrice { get; set; }

    public decimal? CacheReadPrice { get; set; }

    public decimal? CacheWrite5mPrice { get; set; }

    public decimal? CacheWrite1hPrice { get; set; }

    public decimal? OutputPrice { get; set; }

    /// <summary>思考模式的输出单价，null 表示和 <see cref="OutputPrice"/> 相同（ADR-0013）。</summary>
    public decimal? ThinkingOutputPrice { get; set; }
}

/// <summary>
/// 一个 (Provider, UpstreamModel) 的单价（Q48/Q71）。
///
/// 行的来源是所有渠道声明的 (Provider, UpstreamModel) 并集：渠道一加上游模型就建行，
/// 上游模型删了行还留着，免得改配置把填过的单价弄丢。
/// 单价本身按 ADR-0009 在配置里保持 <c>decimal</c>（美元 / 每百万 Token），
/// 只在算 Cost 的那一刻换成纳美元整数。空和 0 不同：0 是免费，空是不知道（Q65）。
/// </summary>
public sealed class PriceRow
{
    public int Id { get; set; }

    /// <summary>供应商 id，小写，对应 <see cref="Configuration.Channel.Provider"/>。</summary>
    public required string Provider { get; set; }

    /// <summary>上游模型名，小写。</summary>
    public required string UpstreamModel { get; set; }

    public PriceSource Source { get; set; } = PriceSource.Auto;

    /// <summary>
    /// 自动行配上的目录条目 id；配不上或有歧义时为空。人工行留着上次自动配上的值做参考。
    /// </summary>
    public string? MatchedModelId { get; set; }

    public decimal? InputPrice { get; set; }

    public decimal? CacheReadPrice { get; set; }

    public decimal? CacheWrite5mPrice { get; set; }

    public decimal? CacheWrite1hPrice { get; set; }

    public decimal? OutputPrice { get; set; }

    /// <summary>
    /// 思考模式的输出单价：上游输出了思考过程时，整个输出都按它算（ADR-0013）。
    /// null 表示和 <see cref="OutputPrice"/> 相同，不是「不知道」。
    /// </summary>
    public decimal? ThinkingOutputPrice { get; set; }

    /// <summary>
    /// 每张图片的单价，美元（Q230）。只能手填：价格目录里没有这一项。
    /// 填了的话图片请求按「张数 × 单价」算，不看 Token。
    /// </summary>
    public decimal? ImagePrice { get; set; }

    /// <summary>长上下文单价，按门槛升序。存成 JSON 列。</summary>
    public List<LongContextPriceEntry> LongContext { get; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>五项基础单价和每张图片单价全空：这一行算不出费用。</summary>
    public bool IsEmpty =>
        ImagePrice is null
        && InputPrice is null
        && CacheReadPrice is null
        && CacheWrite5mPrice is null
        && CacheWrite1hPrice is null
        && OutputPrice is null;

    public PriceSet ToPriceSet() =>
        new(InputPrice, CacheReadPrice, CacheWrite5mPrice, CacheWrite1hPrice, OutputPrice, ThinkingOutputPrice);

    public IReadOnlyList<LongContextPrice> ToLongContext() =>
        LongContext.Count == 0
            ? CatalogPrice.NoLongContext
            : [.. LongContext
                .OrderBy(t => t.InputTokensOver)
                .Select(t => new LongContextPrice(
                    t.InputTokensOver,
                    new PriceSet(
                        t.InputPrice,
                        t.CacheReadPrice,
                        t.CacheWrite5mPrice,
                        t.CacheWrite1hPrice,
                        t.OutputPrice,
                        t.ThinkingOutputPrice)))];

    /// <summary>按目录条目覆盖这一行的单价。只给自动行用。</summary>
    public void CopyFrom(CatalogPrice entry)
    {
        MatchedModelId = entry.ModelId;
        InputPrice = entry.Prices.Input;
        CacheReadPrice = entry.Prices.CacheRead;
        CacheWrite5mPrice = entry.Prices.CacheWrite5m;
        CacheWrite1hPrice = entry.Prices.CacheWrite1h;
        OutputPrice = entry.Prices.Output;
        ThinkingOutputPrice = entry.Prices.ThinkingOutput;
        // 目录里没有每张图片的单价；自动行不带它，「恢复自动」也就清掉了手填的那张单价。
        ImagePrice = null;
        LongContext.Clear();
        foreach (LongContextPrice group in entry.LongContext)
        {
            LongContext.Add(new LongContextPriceEntry
            {
                InputTokensOver = group.InputTokensOver,
                InputPrice = group.Prices.Input,
                CacheReadPrice = group.Prices.CacheRead,
                CacheWrite5mPrice = group.Prices.CacheWrite5m,
                CacheWrite1hPrice = group.Prices.CacheWrite1h,
                OutputPrice = group.Prices.Output,
                ThinkingOutputPrice = group.Prices.ThinkingOutput,
            });
        }
    }

    /// <summary>清掉自动配上的单价：目录里不再有这个模型，或配不上了。</summary>
    public void ClearAutoPrices()
    {
        MatchedModelId = null;
        InputPrice = null;
        CacheReadPrice = null;
        CacheWrite5mPrice = null;
        CacheWrite1hPrice = null;
        OutputPrice = null;
        ThinkingOutputPrice = null;
        ImagePrice = null;
        LongContext.Clear();
    }
}
