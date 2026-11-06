namespace Ntm.Gateway.Pricing;

/// <summary>
/// 一组单价，单位是「美元 / 每百万 Token」（Q51），按 Usage 的五项分列。
/// null 和 0 不是一回事：0 是免费，null 是不知道（Q65）。
/// </summary>
/// <param name="Input">非缓存输入。</param>
/// <param name="CacheRead">缓存读。</param>
/// <param name="CacheWrite5m">5 分钟缓存写。</param>
/// <param name="CacheWrite1h">1 小时缓存写。</param>
/// <param name="Output">输出（没输出思考过程时，思考 Token 也按这个算）。</param>
/// <param name="ThinkingOutput">
/// 思考模式的输出单价：输出了思考过程时，整个输出（思考 + 回答）都按它算（ADR-0013）。
/// 它是唯一一列 null 不表示「不知道」的单价：null 表示和 <paramref name="Output"/> 相同。
/// </param>
public sealed record PriceSet(
    decimal? Input,
    decimal? CacheRead,
    decimal? CacheWrite5m,
    decimal? CacheWrite1h,
    decimal? Output,
    decimal? ThinkingOutput = null)
{
    public static readonly PriceSet Empty = new(null, null, null, null, null);

    /// <summary>五项全空：这一行没有任何单价。思考输出单价单独有值也不算数，它只是输出单价的另一档。</summary>
    public bool IsEmpty =>
        Input is null && CacheRead is null && CacheWrite5m is null && CacheWrite1h is null && Output is null;

    /// <summary>这次实际该用的输出单价。</summary>
    /// <param name="sawThinking">上游这次有没有输出思考过程。</param>
    public decimal? OutputFor(bool sawThinking) => sawThinking ? ThinkingOutput ?? Output : Output;
}

/// <summary>
/// 长上下文单价（Q66）：总输入超过 <paramref name="InputTokensOver"/> 时，
/// 整个请求（含输出）按这一组单价算。
/// </summary>
/// <param name="InputTokensOver">门槛，按总输入 Token 数算。200000 和 200001 都当「超过 200K」。</param>
/// <param name="Prices">这一档的整套单价。</param>
public sealed record LongContextPrice(long InputTokensOver, PriceSet Prices);

/// <summary>价目目录里的一个条目：某个 Provider 下某个模型 id 的单价。</summary>
/// <param name="Provider">供应商 id，小写。</param>
/// <param name="ModelId">目录里的模型 id，小写。</param>
/// <param name="Prices">基础单价。</param>
/// <param name="LongContext">长上下文单价，按门槛升序；没有就是空列表。</param>
public sealed record CatalogPrice(
    string Provider,
    string ModelId,
    PriceSet Prices,
    IReadOnlyList<LongContextPrice> LongContext)
{
    public static readonly IReadOnlyList<LongContextPrice> NoLongContext = [];
}

/// <summary>
/// 配置快照里的一份单价：某个 (Provider, UpstreamModel) 当前生效的基础单价和长上下文单价。
/// 由 <see cref="PriceRow"/> 投影而来，不可变。
/// </summary>
/// <param name="Prices">基础单价。</param>
/// <param name="LongContext">长上下文单价，按门槛升序。</param>
/// <param name="ImagePrice">每张图片的单价，美元（Q230）；null 表示图片也按 Token 算。</param>
public sealed record ModelPrice(PriceSet Prices, IReadOnlyList<LongContextPrice> LongContext, decimal? ImagePrice = null)
{
    /// <summary>按总输入 Token 数挑一组单价：超过门槛的最高那一组，否则基础单价（Q66）。</summary>
    /// <param name="totalInputTokens">非缓存输入 + 缓存读 + 两种缓存写之和。</param>
    /// <returns>这次该用的单价，以及用的是哪个门槛（没用长上下文单价时为 null）。</returns>
    public (PriceSet Prices, long? InputTokensOver) For(long totalInputTokens)
    {
        PriceSet prices = Prices;
        long? threshold = null;
        foreach (LongContextPrice group in LongContext)
        {
            if (totalInputTokens > group.InputTokensOver)
            {
                prices = group.Prices;
                threshold = group.InputTokensOver;
            }
        }

        return (prices, threshold);
    }
}

/// <summary>目录里的一个供应商，只用于渠道编辑页的下拉列表。</summary>
/// <param name="Id">供应商 id，小写。</param>
/// <param name="Name">展示名，目录里没给就等于 id。</param>
/// <param name="ModelCount">这个供应商下有单价的模型数。</param>
public sealed record CatalogProvider(string Id, string Name, int ModelCount);
