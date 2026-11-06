using Microsoft.AspNetCore.Http.HttpResults;
using Ntm.Gateway.Pricing;

namespace Ntm.Server.Admin;

/// <summary>目录里的一个供应商，渠道编辑页的下拉列表用（Q71）。</summary>
internal sealed record CatalogProviderView(string Id, string Name, int ModelCount);

/// <summary>价目目录的状态，单价页页头用（Q69/Q70）。</summary>
/// <param name="FetchedAt">上次成功取到目录的时间；从没成功过是 null，页面说明「还没取到目录」。</param>
/// <param name="LastAttemptAt">上次尝试的时间。</param>
/// <param name="LastError">上次失败的原因。</param>
/// <param name="AutoRefresh">自动刷新是否开着。</param>
/// <param name="Refreshing">是否正在刷新。</param>
/// <param name="ProviderCount">目录里的供应商数。</param>
internal sealed record CatalogStatusView(
    DateTimeOffset? FetchedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    bool AutoRefresh,
    bool Refreshing,
    int ProviderCount);

/// <param name="Status">目录状态。</param>
/// <param name="Providers">目录里所有供应商，按 id 排序。</param>
internal sealed record CatalogView(CatalogStatusView Status, IReadOnlyList<CatalogProviderView> Providers);

/// <summary>价目目录的状态与刷新（Q69/Q70/Q71）。单价行本身在 PriceEndpoints。</summary>
internal static class PriceCatalogEndpoints
{
    public static RouteGroupBuilder MapPriceCatalogEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", Get);
        group.MapPost("/refresh", RefreshAsync);
        return group;
    }

    private static Ok<CatalogView> Get(PriceCatalogService catalog) =>
        TypedResults.Ok(new CatalogView(
            ToStatus(catalog),
            [.. catalog.Current.Providers.Select(p => new CatalogProviderView(p.Id, p.Name, p.ModelCount))]));

    /// <summary>「立即刷新」。等这次下载结束再回，页面好直接显示结果。</summary>
    private static async Task<Ok<CatalogStatusView>> RefreshAsync(
        PriceCatalogService catalog,
        CancellationToken cancellationToken)
    {
        await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToStatus(catalog));
    }

    private static CatalogStatusView ToStatus(PriceCatalogService catalog)
    {
        PriceCatalogStatus status = catalog.Status;
        return new CatalogStatusView(
            status.FetchedAt,
            status.LastAttemptAt,
            status.LastError,
            status.AutoRefresh,
            status.Refreshing,
            catalog.Current.Providers.Count);
    }
}
