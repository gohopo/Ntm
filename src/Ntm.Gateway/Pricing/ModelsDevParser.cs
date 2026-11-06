using System.Text.Json;

namespace Ntm.Gateway.Pricing;

/// <param name="Providers">目录里所有供应商（id、名字、模型数）。</param>
/// <param name="Prices">只有被要求保留的那几个供应商的单价（Q72）。</param>
public sealed record ParsedCatalog(
    IReadOnlyList<CatalogProvider> Providers,
    IReadOnlyList<CatalogPrice> Prices);

/// <summary>
/// 解析 models.dev 的 api.json（Q69）。结构是 provider id → { id, name, models: { model id → { cost } } }。
///
/// 字段对应（Q65）：cost.input → 非缓存输入，cost.cache_read → 缓存读，
/// cost.cache_write → 5 分钟缓存写，cost.output → 输出。单位都是美元 / 每百万 Token。
/// cost.reasoning 只在高于 cost.output 时取作思考模式的输出单价（ADR-0013）：阿里系的这一列是
/// 「输出了思考过程时整个输出的单价」；更低的值是别的规则（Sonar 的推理 Token 单价）或占位（kilo 的 0），不取。
/// 不用两个音频字段和 experimental.modes。
///
/// 1 小时缓存写目录里没有。Anthropic 系（模型名里有 claude 且登记了缓存读）按输入价的 2 倍推出，
/// 其他留空。这个倍数没能核实官方文档，只是业内普遍说法。
/// </summary>
public static class ModelsDevParser
{
    /// <summary>门槛归一化（Q66）：200000 和 200001 都当「超过 200K」。</summary>
    internal static long NormalizeThreshold(long raw) => raw > 0 && raw % 1000 == 1 ? raw - 1 : raw;

    /// <summary>流式解析。<paramref name="keepProviders"/> 为空表示一个单价都不留。</summary>
    public static async Task<ParsedCatalog> ParseAsync(
        Stream json,
        IReadOnlySet<string> keepProviders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(keepProviders);

        using JsonDocument document = await JsonDocument
            .ParseAsync(json, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var providers = new List<CatalogProvider>();
        var prices = new List<CatalogPrice>();

        foreach (JsonProperty provider in document.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string id = (Text(provider.Value, "id") ?? provider.Name).ToLowerInvariant();
            bool keep = keepProviders.Contains(id);

            int count = 0;
            if (provider.Value.TryGetProperty("models", out JsonElement models)
                && models.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty model in models.EnumerateObject())
                {
                    if (model.Value.ValueKind != JsonValueKind.Object
                        || !model.Value.TryGetProperty("cost", out JsonElement cost)
                        || cost.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    PriceSet basePrices = ReadPrices(cost, fallback: null);
                    string modelId = (Text(model.Value, "id") ?? model.Name).ToLowerInvariant();
                    if (basePrices.IsEmpty)
                    {
                        continue;
                    }

                    count++;
                    if (keep)
                    {
                        PriceSet derived = WithDerivedCacheWrite1h(modelId, basePrices);
                        prices.Add(new CatalogPrice(
                            id,
                            modelId,
                            derived,
                            [.. ReadLongContext(cost, basePrices).Select(tier =>
                                tier with { Prices = WithDerivedCacheWrite1h(modelId, tier.Prices) })]));
                    }
                }
            }

            providers.Add(new CatalogProvider(id, Text(provider.Value, "name") ?? id, count));
        }

        return new ParsedCatalog(providers, prices);
    }

    /// <summary>
    /// 1 小时缓存写：目录里没有这一项，Anthropic 系按输入价的 2 倍推出（Q65）。
    /// 判断看模型名里有没有 claude，而不是看供应商：中转商下面的 claude 行同样适用。
    /// 还要求登记了缓存读，确认这个模型真的支持缓存。
    /// </summary>
    private static PriceSet WithDerivedCacheWrite1h(string modelId, PriceSet prices)
    {
        if (prices.CacheWrite1h is not null
            || prices.Input is not { } input
            || prices.CacheRead is null
            || !modelId.Contains("claude", StringComparison.Ordinal))
        {
            return prices;
        }

        return prices with { CacheWrite1h = input * 2m };
    }

    /// <summary>
    /// 长上下文单价（Q66）。cost.tiers 是正式字段，cost.context_over_200k 是 tiers[0] 的旧写法，
    /// 两者按归一化后的门槛去重。
    ///
    /// 某一档没给的那一项用基础单价顶上：厂商只列变了的项，照搬「空 = 不知道」会把
    /// 长上下文下的缓存写错判成无单价。
    /// </summary>
    private static IReadOnlyList<LongContextPrice> ReadLongContext(JsonElement cost, PriceSet basePrices)
    {
        var byThreshold = new SortedDictionary<long, PriceSet>();

        if (cost.TryGetProperty("tiers", out JsonElement tiers) && tiers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tier in tiers.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.Object
                    || !tier.TryGetProperty("tier", out JsonElement shape)
                    || shape.ValueKind != JsonValueKind.Object
                    || Text(shape, "type") is not "context"
                    || !shape.TryGetProperty("size", out JsonElement size)
                    || !size.TryGetInt64(out long raw))
                {
                    continue;
                }

                byThreshold[NormalizeThreshold(raw)] = ReadPrices(tier, basePrices);
            }
        }

        if (cost.TryGetProperty("context_over_200k", out JsonElement legacy)
            && legacy.ValueKind == JsonValueKind.Object
            && !byThreshold.ContainsKey(200_000))
        {
            byThreshold[200_000] = ReadPrices(legacy, basePrices);
        }

        if (byThreshold.Count == 0)
        {
            return CatalogPrice.NoLongContext;
        }

        return [.. byThreshold.Select(entry => new LongContextPrice(entry.Key, entry.Value))];
    }

    private static PriceSet ReadPrices(JsonElement cost, PriceSet? fallback)
    {
        decimal? output = Number(cost, "output") ?? fallback?.Output;
        decimal? reasoning = Number(cost, "reasoning") ?? fallback?.ThinkingOutput;
        return new(
            Number(cost, "input") ?? fallback?.Input,
            Number(cost, "cache_read") ?? fallback?.CacheRead,
            Number(cost, "cache_write") ?? fallback?.CacheWrite5m,
            // 目录里没有 1 小时缓存写；每一档各自按自己的输入价推（WithDerivedCacheWrite1h）。
            null,
            output,
            // 宁可多收不少收：只认比输出价高的（ADR-0013）。
            reasoning is { } r && output is { } o && r > o ? r : null);
    }

    private static decimal? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out decimal number)
                ? number
                : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
