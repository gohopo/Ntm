using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;

namespace Ntm.Server.Admin;

/// <summary>一组单价，基础的和长上下文的都用它（Q66）。</summary>
/// <param name="ThinkingOutputPrice">思考模式的输出单价；null 表示和输出单价相同（ADR-0013）。</param>
internal sealed record PriceSetView(
    decimal? InputPrice,
    decimal? CacheReadPrice,
    decimal? CacheWrite5mPrice,
    decimal? CacheWrite1hPrice,
    decimal? OutputPrice,
    decimal? ThinkingOutputPrice = null);

/// <param name="InputTokensOver">总输入超过这个数就按这一组算。</param>
internal sealed record LongContextPriceView(long InputTokensOver, PriceSetView Prices);

/// <summary>单价页的一行（Q70）。</summary>
/// <param name="Unpriced">五项基础单价和每张图片单价全空：这个模型算不出费用。</param>
/// <param name="ImagePrice">每张图片的单价，美元（Q230）。只能手填。</param>
/// <param name="Candidates">目录里并列的候选模型 id，有歧义时才有（Q68）。</param>
/// <param name="Channels">用着这个 (Provider, 上游模型) 的渠道名，按名字排序。</param>
/// <param name="UnpricedRequests">近 7 天因为没单价而算不出费用的请求数（Q74，按渠道当前的 Provider 归行）。</param>
internal sealed record PriceRowView(
    int Id,
    string Provider,
    string UpstreamModel,
    PriceSource Source,
    bool Unpriced,
    string? MatchedModelId,
    IReadOnlyList<string> Candidates,
    PriceSetView Prices,
    IReadOnlyList<LongContextPriceView> LongContext,
    IReadOnlyList<string> Channels,
    long UnpricedRequests,
    DateTimeOffset UpdatedAt,
    decimal? ImagePrice = null);

internal sealed record PricePageView(IReadOnlyList<PriceRowView> Items, int Total, int Page, int PageSize);

/// <summary>改单价的入参。省略的字段按 null 处理：null 是「不知道」，0 是免费（Q65）。</summary>
internal sealed record PriceInput(
    decimal? InputPrice,
    decimal? CacheReadPrice,
    decimal? CacheWrite5mPrice,
    decimal? CacheWrite1hPrice,
    decimal? OutputPrice,
    IReadOnlyList<LongContextPriceView>? LongContext,
    decimal? ThinkingOutputPrice = null,
    decimal? ImagePrice = null);

/// <summary>单价行：列表、改、恢复自动、删（Q48/Q65/Q66/Q70/Q74）。</summary>
internal static class PriceEndpoints
{
    private const int PageSize = 100;
    private const decimal MaxPrice = 100_000m;
    private const int MaxDecimals = 6;
    private const int MaxLongContextGroups = 10;

    public static RouteGroupBuilder MapPriceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPost("/{id:int}/auto", RestoreAutoAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return group;
    }

    /// <summary>
    /// 分页列出单价行。默认排序：无单价优先，其次近 7 天 Attempt 数降序，再按名字（Q74）。
    /// </summary>
    /// <param name="filter">all（默认）/ unpriced / manual / unused。</param>
    /// <param name="search">按 Provider 或上游模型名模糊搜。</param>
    private static async Task<IResult> ListAsync(
        ConfigDbContext db,
        TrafficDbContext traffic,
        PriceCatalogService catalog,
        TimeProvider timeProvider,
        string? filter = null,
        string? search = null,
        int page = 1)
    {
        var errors = new ValidationErrors();
        if (page < 1)
        {
            errors.Add("page", "页码从 1 开始。");
        }

        string kind = (filter ?? "all").Trim().ToLowerInvariant();
        if (kind is not ("all" or "unpriced" or "manual" or "unused"))
        {
            errors.Add("filter", "只能按全部、无单价、人工或没被使用筛选。");
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        List<PriceRow> rows = await db.PriceRows.AsNoTracking().ToListAsync().ConfigureAwait(false);

        // 哪些渠道在用这一行，以及近 7 天的无单价次数。渠道数和单价行数都是几百的量级，
        // 一次全读出来在内存里配，比按行查库简单得多。
        Dictionary<(string Provider, string Model), List<string>> usage = await UsageAsync(db).ConfigureAwait(false);
        Dictionary<(string Provider, string Model), long> unpricedRequests =
            await UnpricedRequestsAsync(db, traffic, timeProvider).ConfigureAwait(false);

        PriceCatalog current = catalog.Current;
        List<PriceRowView> views =
        [
            .. rows.Select(row =>
            {
                (string, string) key = (row.Provider, row.UpstreamModel);
                List<string> channels = usage.TryGetValue(key, out List<string>? names) ? names : [];
                PriceMatch match = row.Source == PriceSource.Auto && row.IsEmpty
                    ? PriceMatcher.Match(row.UpstreamModel, current.PricesOf(row.Provider))
                    : PriceMatch.None;
                return ToView(
                    row,
                    channels,
                    unpricedRequests.TryGetValue(key, out long count) ? count : 0,
                    match.Kind == PriceMatchKind.Ambiguous ? match.Candidates : []);
            }),
        ];

        IEnumerable<PriceRowView> filtered = kind switch
        {
            "unpriced" => views.Where(v => v.Unpriced),
            "manual" => views.Where(v => v.Source == PriceSource.Manual),
            "unused" => views.Where(v => v.Channels.Count == 0),
            _ => views,
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            string needle = search.Trim();
            filtered = filtered.Where(v =>
                v.UpstreamModel.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || v.Provider.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        List<PriceRowView> ordered =
        [
            .. filtered
                .OrderByDescending(v => v.Unpriced)
                .ThenByDescending(v => v.UnpricedRequests)
                .ThenBy(v => v.Provider, StringComparer.Ordinal)
                .ThenBy(v => v.UpstreamModel, StringComparer.Ordinal),
        ];

        return TypedResults.Ok(new PricePageView(
            [.. ordered.Skip((page - 1) * PageSize).Take(PageSize)],
            ordered.Count,
            page,
            PageSize));
    }

    /// <summary>改单价。改任意一项就变人工行，之后刷新不再覆盖它（Q69/Q70）。</summary>
    private static async Task<IResult> UpdateAsync(
        int id,
        PriceInput input,
        ConfigDbContext db,
        TrafficDbContext traffic,
        ConfigSnapshotProvider snapshots,
        PriceCatalogService catalog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(input);

        PriceRow? row = await db.PriceRows.FirstOrDefaultAsync(r => r.Id == id).ConfigureAwait(false);
        if (row is null)
        {
            return AdminResults.NotFound("单价");
        }

        var errors = new ValidationErrors();
        Validate(errors, "prices", input.InputPrice, input.CacheReadPrice, input.CacheWrite5mPrice, input.CacheWrite1hPrice, input.OutputPrice, input.ThinkingOutputPrice, input.ImagePrice);

        IReadOnlyList<LongContextPriceView> groups = input.LongContext ?? [];
        if (groups.Count > MaxLongContextGroups)
        {
            errors.Add("longContext", $"长上下文单价最多 {MaxLongContextGroups} 组。");
        }

        long previous = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            LongContextPriceView group = groups[i];
            if (group.InputTokensOver <= 0)
            {
                errors.Add($"longContext[{i}].inputTokensOver", "门槛要大于 0。");
            }
            else if (group.InputTokensOver <= previous)
            {
                errors.Add($"longContext[{i}].inputTokensOver", "门槛要比上一组大。");
            }

            previous = group.InputTokensOver;
            PriceSetView prices = group.Prices;
            Validate(
                errors,
                $"longContext[{i}].prices",
                prices.InputPrice,
                prices.CacheReadPrice,
                prices.CacheWrite5mPrice,
                prices.CacheWrite1hPrice,
                prices.OutputPrice,
                prices.ThinkingOutputPrice);
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        row.Source = PriceSource.Manual;
        row.InputPrice = input.InputPrice;
        row.CacheReadPrice = input.CacheReadPrice;
        row.CacheWrite5mPrice = input.CacheWrite5mPrice;
        row.CacheWrite1hPrice = input.CacheWrite1hPrice;
        row.OutputPrice = input.OutputPrice;
        row.ThinkingOutputPrice = input.ThinkingOutputPrice;
        row.ImagePrice = input.ImagePrice;
        row.LongContext.Clear();
        foreach (LongContextPriceView group in groups.OrderBy(g => g.InputTokensOver))
        {
            row.LongContext.Add(new LongContextPriceEntry
            {
                InputTokensOver = group.InputTokensOver,
                InputPrice = group.Prices.InputPrice,
                CacheReadPrice = group.Prices.CacheReadPrice,
                CacheWrite5mPrice = group.Prices.CacheWrite5mPrice,
                CacheWrite1hPrice = group.Prices.CacheWrite1hPrice,
                OutputPrice = group.Prices.OutputPrice,
                ThinkingOutputPrice = group.Prices.ThinkingOutputPrice,
            });
        }

        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync().ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);

        return TypedResults.Ok(await ViewAsync(db, traffic, catalog, timeProvider, row).ConfigureAwait(false));
    }

    /// <summary>「恢复自动」：马上按当前目录重新匹配这一行（Q70）。</summary>
    private static async Task<IResult> RestoreAutoAsync(
        int id,
        ConfigDbContext db,
        TrafficDbContext traffic,
        ConfigSnapshotProvider snapshots,
        PriceCatalogService catalog,
        TimeProvider timeProvider)
    {
        PriceRow? row = await db.PriceRows.FirstOrDefaultAsync(r => r.Id == id).ConfigureAwait(false);
        if (row is null)
        {
            return AdminResults.NotFound("单价");
        }

        row.Source = PriceSource.Auto;
        if (PriceMatcher.TryMatch(catalog.Current, row.Provider, row.UpstreamModel, out CatalogPrice? entry))
        {
            row.CopyFrom(entry);
        }
        else
        {
            row.ClearAutoPrices();
        }

        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync().ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);

        return TypedResults.Ok(await ViewAsync(db, traffic, catalog, timeProvider, row).ConfigureAwait(false));
    }

    /// <summary>删掉没被使用的行。还在用的不能删，删了下次保存渠道又会建回来（Q70）。</summary>
    private static async Task<IResult> DeleteAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots)
    {
        PriceRow? row = await db.PriceRows.FirstOrDefaultAsync(r => r.Id == id).ConfigureAwait(false);
        if (row is null)
        {
            return AdminResults.NotFound("单价");
        }

        Dictionary<(string Provider, string Model), List<string>> usage = await UsageAsync(db).ConfigureAwait(false);
        if (usage.ContainsKey((row.Provider, row.UpstreamModel)))
        {
            return AdminResults.Conflict("这个上游模型还在被渠道使用，删不掉。");
        }

        db.PriceRows.Remove(row);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>(Provider, 上游模型) → 用着它的渠道名。停用的渠道也算，它随时会被启用。</summary>
    private static async Task<Dictionary<(string Provider, string Model), List<string>>> UsageAsync(ConfigDbContext db)
    {
        var pairs = await (
            from channel in db.Channels
            join model in db.UpstreamModels on channel.Id equals model.ChannelId
            where channel.Provider != ""
            select new { channel.Provider, Channel = channel.Name, Model = model.Name })
            .ToListAsync()
            .ConfigureAwait(false);

        var usage = new Dictionary<(string Provider, string Model), List<string>>();
        foreach (var pair in pairs)
        {
            (string, string) key = (pair.Provider.ToLowerInvariant(), pair.Model.ToLowerInvariant());
            if (!usage.TryGetValue(key, out List<string>? names))
            {
                names = [];
                usage[key] = names;
            }

            if (!names.Contains(pair.Channel))
            {
                names.Add(pair.Channel);
            }
        }

        foreach (List<string> names in usage.Values)
        {
            names.Sort(StringComparer.Ordinal);
        }

        return usage;
    }

    /// <summary>
    /// 近 7 天没算出费用的 Attempt 数，按 (渠道当前的 Provider, 上游模型) 归行。
    /// 汇总没有 Provider 维度，所以渠道改过 Provider 的历史会算到新 Provider 名下（Q74，接受这点误差）。
    /// </summary>
    private static async Task<Dictionary<(string Provider, string Model), long>> UnpricedRequestsAsync(
        ConfigDbContext db,
        TrafficDbContext traffic,
        TimeProvider timeProvider)
    {
        Dictionary<int, string> providers = await db.Channels
            .Where(c => c.Provider != "")
            .ToDictionaryAsync(c => c.Id, c => c.Provider.ToLowerInvariant())
            .ConfigureAwait(false);

        var counts = new Dictionary<(string Provider, string Model), long>();
        if (providers.Count == 0)
        {
            return counts;
        }

        DateTimeOffset since = timeProvider.GetUtcNow().AddDays(-7);
        var rows = await traffic.HourlyAggregates
            .AsNoTracking()
            .Where(a => a.HourUtc >= since && a.UnpricedCount > 0)
            .GroupBy(a => new { a.ChannelId, a.UpstreamModel })
            .Select(g => new { g.Key.ChannelId, g.Key.UpstreamModel, Count = g.Sum(a => a.UnpricedCount) })
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (!providers.TryGetValue(row.ChannelId, out string? provider))
            {
                // 渠道已经删了：没有当前 Provider 可归，跳过。
                continue;
            }

            (string, string) key = (provider, row.UpstreamModel.ToLowerInvariant());
            counts[key] = counts.GetValueOrDefault(key) + row.Count;
        }

        return counts;
    }

    private static async Task<PriceRowView> ViewAsync(
        ConfigDbContext db,
        TrafficDbContext traffic,
        PriceCatalogService catalog,
        TimeProvider timeProvider,
        PriceRow row)
    {
        Dictionary<(string Provider, string Model), List<string>> usage = await UsageAsync(db).ConfigureAwait(false);
        Dictionary<(string Provider, string Model), long> unpriced =
            await UnpricedRequestsAsync(db, traffic, timeProvider).ConfigureAwait(false);
        (string, string) key = (row.Provider, row.UpstreamModel);
        PriceMatch match = row.Source == PriceSource.Auto && row.IsEmpty
            ? PriceMatcher.Match(row.UpstreamModel, catalog.Current.PricesOf(row.Provider))
            : PriceMatch.None;
        return ToView(
            row,
            usage.TryGetValue(key, out List<string>? names) ? names : [],
            unpriced.TryGetValue(key, out long count) ? count : 0,
            match.Kind == PriceMatchKind.Ambiguous ? match.Candidates : []);
    }

    private static PriceRowView ToView(
        PriceRow row,
        IReadOnlyList<string> channels,
        long unpricedRequests,
        IReadOnlyList<string> candidates) =>
        new(
            row.Id,
            row.Provider,
            row.UpstreamModel,
            row.Source,
            row.IsEmpty,
            row.MatchedModelId,
            candidates,
            new PriceSetView(
                row.InputPrice,
                row.CacheReadPrice,
                row.CacheWrite5mPrice,
                row.CacheWrite1hPrice,
                row.OutputPrice,
                row.ThinkingOutputPrice),
            [
                .. row.LongContext
                    .OrderBy(g => g.InputTokensOver)
                    .Select(g => new LongContextPriceView(
                        g.InputTokensOver,
                        new PriceSetView(
                            g.InputPrice,
                            g.CacheReadPrice,
                            g.CacheWrite5mPrice,
                            g.CacheWrite1hPrice,
                            g.OutputPrice,
                            g.ThinkingOutputPrice))),
            ],
            channels,
            unpricedRequests,
            row.UpdatedAt,
            row.ImagePrice);

    private static void Validate(ValidationErrors errors, string prefix, params decimal?[] prices)
    {
        string[] names = ["inputPrice", "cacheReadPrice", "cacheWrite5mPrice", "cacheWrite1hPrice", "outputPrice", "thinkingOutputPrice", "imagePrice"];
        for (int i = 0; i < prices.Length; i++)
        {
            if (prices[i] is not { } price)
            {
                continue;
            }

            string field = prefix == "prices" ? names[i] : prefix + "." + names[i];
            if (price < 0)
            {
                errors.Add(field, "单价不能是负数。");
            }
            else if (price > MaxPrice)
            {
                errors.Add(field, $"单价不能超过 {MaxPrice:N0}。");
            }
            else if (decimal.Round(price, MaxDecimals) != price)
            {
                errors.Add(field, $"单价最多 {MaxDecimals} 位小数。");
            }
        }
    }
}
