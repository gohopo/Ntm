using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 维护单价行（Q48）：行 = 所有渠道声明的 (Provider, UpstreamModel) 并集。
///
/// 渠道保存后 <see cref="SyncAsync"/> 补齐缺的行；价目目录刷新后
/// <see cref="RematchAsync"/> 把自动行重新匹配一遍，人工行一律不动（Q69）。
/// 行只增不减：上游模型删了行还留着，免得把人工填过的单价弄丢。
/// </summary>
public sealed class PriceRowService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<PriceRowService> logger)
{
    private static readonly Action<ILogger, int, Exception?> LogAdded =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(6101, "PriceRowsAdded"),
            "补了 {Count} 行单价。");

    private static readonly Action<ILogger, int, int, Exception?> LogRematched =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(6102, "PriceRowsRematched"),
            "价目目录更新后重新匹配：{Changed} 行有变化，共 {Total} 个自动行。");

    /// <summary>
    /// 按当前渠道配置补齐单价行，并给新行配上目录里的单价。返回新增的行数。
    /// </summary>
    /// <param name="current">当前价目目录；还没取到就传 <see cref="PriceCatalog.Empty"/>，新行先留空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> SyncAsync(PriceCatalog current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        // 停用的渠道也算：停用不是删除，单价不该跟着消失。
        // 用显式 join：SQLite 不支持 SelectMany 导航生成的 APPLY。
        var wanted = await (
                from channel in db.Channels
                join model in db.UpstreamModels on channel.Id equals model.ChannelId
                where channel.Provider != ""
                select new { channel.Provider, model.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keys = new HashSet<(string Provider, string Model)>();
        foreach (var pair in wanted)
        {
            keys.Add((pair.Provider.ToLowerInvariant(), pair.Name.ToLowerInvariant()));
        }

        if (keys.Count == 0)
        {
            return 0;
        }

        List<PriceRow> rows = await db.PriceRows.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (PriceRow row in rows)
        {
            keys.Remove((row.Provider, row.UpstreamModel));
        }

        if (keys.Count == 0)
        {
            return 0;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach ((string provider, string model) in keys)
        {
            var row = new PriceRow
            {
                Provider = provider,
                UpstreamModel = model,
                Source = PriceSource.Auto,
                CreatedAt = now,
                UpdatedAt = now,
            };

            if (PriceMatcher.TryMatch(current, provider, model, out CatalogPrice? entry))
            {
                row.CopyFrom(entry);
            }

            db.PriceRows.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogAdded(logger, keys.Count, null);
        return keys.Count;
    }

    /// <summary>目录换了之后，把自动行按新目录重新匹配。人工行不动（Q69）。返回有变化的行数。</summary>
    public async Task<int> RematchAsync(PriceCatalog current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        List<PriceRow> rows = await db.PriceRows
            .Where(r => r.Source == PriceSource.Auto)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = 0;
        foreach (PriceRow row in rows)
        {
            // 这次目录里没有这个供应商：留着上次的单价，别把它清成无单价。
            if (!current.Providers.Any(p => p.Id == row.Provider))
            {
                continue;
            }

            PriceSet before = row.ToPriceSet();
            IReadOnlyList<LongContextPrice> longBefore = row.ToLongContext();

            if (PriceMatcher.TryMatch(current, row.Provider, row.UpstreamModel, out CatalogPrice? entry))
            {
                row.CopyFrom(entry);
            }
            else
            {
                row.ClearAutoPrices();
            }

            if (before != row.ToPriceSet() || !longBefore.SequenceEqual(row.ToLongContext()))
            {
                row.UpdatedAt = now;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        LogRematched(logger, changed, rows.Count, null);
        return changed;
    }
}
