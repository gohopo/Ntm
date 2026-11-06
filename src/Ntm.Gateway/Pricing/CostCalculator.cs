using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 把 Usage 的五项换成纳美元（ADR-0009、Q65/Q66）。
///
/// 规则：某一项 Token 不为 0 而对应单价为空时，整次算不出费用（返回 null），
/// 不拿别的单价顶替——宁可显示「无单价」，也不给一个假数。
/// 唯一的例外是思考模式的输出单价：它没填就是「和输出单价相同」（ADR-0013）。
/// 图片请求填了每张单价时按张算，不看 Token（Q230）。
/// </summary>
public static class CostCalculator
{
    private const decimal PerMillion = 1_000_000m;
    private const decimal NanoPerUsd = 1_000_000_000m;
    private const int BasisPointsOne = 10_000;

    /// <summary>
    /// 算这一次 Attempt 的费用，并把用到的单价快照写回它（Q49）。
    /// </summary>
    /// <param name="attempt">已经填好五项 Token 的 Attempt。</param>
    /// <param name="price">这个 (Provider, 上游模型) 当前的单价；没有就传 null。</param>
    public static void Apply(AttemptRecord attempt, ModelPrice? price)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        attempt.InputPrice = null;
        attempt.CacheReadPrice = null;
        attempt.CacheWrite5mPrice = null;
        attempt.CacheWrite1hPrice = null;
        attempt.OutputPrice = null;
        attempt.LongContextOver = null;
        attempt.ImagePrice = null;
        attempt.CostNanoUsd = null;

        if (price is null)
        {
            return;
        }

        int basisPoints = attempt.MultiplierBasisPoints <= 0 ? BasisPointsOne : attempt.MultiplierBasisPoints;

        // 图片按张计价（Q230）：填了每张单价就只看张数，不看 Token。
        if (attempt.ImageCount > 0 && price.ImagePrice is { } perImage)
        {
            attempt.ImagePrice = perImage;
            attempt.CostNanoUsd = ToNano(attempt.ImageCount * perImage, basisPoints);
            return;
        }

        // 出了图却没有用量也没有每张单价：五项都是 0 不代表免费，算不出来。
        if (attempt.ImageCount > 0 && attempt.UsageSource == Protocol.UsageSource.Unavailable)
        {
            return;
        }

        // 长上下文按总输入分组：非缓存输入 + 缓存读 + 两种缓存写（Q66）。
        long totalInput = attempt.InputTokens
            + attempt.CacheReadTokens
            + attempt.CacheWrite5mTokens
            + attempt.CacheWrite1hTokens;
        (PriceSet prices, long? over) = price.For(totalInput);

        attempt.InputPrice = prices.Input;
        attempt.CacheReadPrice = prices.CacheRead;
        attempt.CacheWrite5mPrice = prices.CacheWrite5m;
        attempt.CacheWrite1hPrice = prices.CacheWrite1h;
        // 输出了思考过程就整个输出换一档单价（ADR-0013）；快照里记的是实际用的那一档。
        attempt.OutputPrice = prices.OutputFor(attempt.SawThinking);
        attempt.LongContextOver = over;

        decimal usd = 0m;
        if (!TryAdd(ref usd, attempt.InputTokens, prices.Input)
            || !TryAdd(ref usd, attempt.CacheReadTokens, prices.CacheRead)
            || !TryAdd(ref usd, attempt.CacheWrite5mTokens, prices.CacheWrite5m)
            || !TryAdd(ref usd, attempt.CacheWrite1hTokens, prices.CacheWrite1h)
            || !TryAdd(ref usd, attempt.OutputTokens, attempt.OutputPrice))
        {
            return;
        }

        attempt.CostNanoUsd = ToNano(usd, basisPoints);
    }

    private static long ToNano(decimal usd, int basisPoints) =>
        (long)decimal.Round(usd * NanoPerUsd * basisPoints / BasisPointsOne, MidpointRounding.AwayFromZero);

    /// <summary>累加一项。Token 为 0 时单价是空的也没关系；不为 0 又没单价就算不出来。</summary>
    private static bool TryAdd(ref decimal usd, long tokens, decimal? pricePerMillion)
    {
        if (tokens == 0)
        {
            return true;
        }

        if (pricePerMillion is not { } price)
        {
            return false;
        }

        usd += tokens / PerMillion * price;
        return true;
    }
}
