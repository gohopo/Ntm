using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 价目目录换了一份之后：自动单价行重新匹配，有变化就重建配置快照——计费读的是快照里的单价。
/// </summary>
public sealed class PriceRowCatalogListener(
    PriceRowService rows,
    ConfigSnapshotProvider snapshots) : IPriceCatalogListener
{
    public async Task OnCatalogChangedAsync(PriceCatalog current, CancellationToken cancellationToken)
    {
        int changed = await rows.RematchAsync(current, cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            await snapshots.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
