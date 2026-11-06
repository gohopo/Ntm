using System.Diagnostics.CodeAnalysis;

namespace Ntm.Gateway.Pricing;

/// <summary>匹配结果（Q48/Q68）。</summary>
public enum PriceMatchKind
{
    /// <summary>这个 Provider 下没有任何条目的段串能连续出现在上游模型名里。</summary>
    NoMatch = 0,

    /// <summary>配上了唯一一个条目。</summary>
    Matched = 1,

    /// <summary>并列最长的条目有多个且单价不同：记为无单价，把候选列出来让人工选（Q70）。</summary>
    Ambiguous = 2,
}

/// <param name="Kind">结果类型。</param>
/// <param name="Entry">配上的条目；并列或配不上时为 null。</param>
/// <param name="Candidates">并列时的候选模型 id，按字典序；其他情况为空。</param>
public sealed record PriceMatch(
    PriceMatchKind Kind,
    CatalogPrice? Entry,
    IReadOnlyList<string> Candidates)
{
    public static readonly PriceMatch None = new(PriceMatchKind.NoMatch, null, []);
}

/// <summary>
/// 把上游模型名配到价目目录的条目上（Q68，被 Q71 修正为限定在渠道声明的 Provider 内）。
///
/// 规则：名字按非字母数字切段（所以 claude-sonnet-4.5 和 claude-sonnet-4-5 同名）；
/// 候选条目的整串段要连续出现在上游模型名的段序列里；段数最多的胜出；
/// 并列且单价不同记为有歧义，并列但单价相同随便取一个。
///
/// 不做反向匹配（不会用 gpt-4 去配 gpt-4.1），也不做别名表：
/// deepseek-chat 这类名字在目录里配不上，留给人工填。
/// </summary>
public static class PriceMatcher
{
    /// <summary>把名字切成小写的字母数字段。</summary>
    public static string[] Segments(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        var segments = new List<string>();
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool alphanumeric = i < name.Length && char.IsAsciiLetterOrDigit(name[i]);
            if (alphanumeric && start < 0)
            {
                start = i;
            }
            else if (!alphanumeric && start >= 0)
            {
                segments.Add(name[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        return [.. segments];
    }

    /// <summary>
    /// 在 <paramref name="candidates"/> 里找最长的连续段匹配。
    /// <paramref name="candidates"/> 应该只包含渠道声明的那个 Provider 下的条目。
    /// </summary>
    public static PriceMatch Match(string upstreamModel, IEnumerable<CatalogPrice> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        string[] target = Segments(upstreamModel);
        if (target.Length == 0)
        {
            return PriceMatch.None;
        }

        var best = new List<CatalogPrice>();
        int bestLength = 0;

        foreach (CatalogPrice candidate in candidates)
        {
            string[] segments = Segments(candidate.ModelId);
            if (segments.Length == 0 || segments.Length < bestLength || !Contains(target, segments))
            {
                continue;
            }

            if (segments.Length > bestLength)
            {
                bestLength = segments.Length;
                best.Clear();
            }

            best.Add(candidate);
        }

        if (best.Count == 0)
        {
            return PriceMatch.None;
        }

        if (best.Count == 1)
        {
            return new PriceMatch(PriceMatchKind.Matched, best[0], []);
        }

        // 并列：单价完全一样就无所谓选哪个（同一个模型在目录里重复登记）。
        CatalogPrice first = best[0];
        bool samePrice = best.TrueForAll(entry =>
            entry.Prices == first.Prices && entry.LongContext.SequenceEqual(first.LongContext));

        return samePrice
            ? new PriceMatch(PriceMatchKind.Matched, first, [])
            : new PriceMatch(
                PriceMatchKind.Ambiguous,
                null,
                [.. best.Select(e => e.ModelId).Order(StringComparer.Ordinal)]);
    }

    /// <summary><paramref name="needle"/> 的整串段是否连续出现在 <paramref name="haystack"/> 里。</summary>
    private static bool Contains(string[] haystack, string[] needle)
    {
        if (needle.Length > haystack.Length)
        {
            return false;
        }

        for (int offset = 0; offset + needle.Length <= haystack.Length; offset++)
        {
            bool hit = true;
            for (int i = 0; i < needle.Length; i++)
            {
                if (!string.Equals(haystack[offset + i], needle[i], StringComparison.Ordinal))
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>便捷重载：在整份目录里按 Provider 过滤后匹配。</summary>
    public static bool TryMatch(
        PriceCatalog catalog,
        string provider,
        string upstreamModel,
        [NotNullWhen(true)] out CatalogPrice? entry)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        PriceMatch match = Match(upstreamModel, catalog.PricesOf(provider));
        entry = match.Entry;
        return match.Kind == PriceMatchKind.Matched;
    }
}
