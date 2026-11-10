namespace Ntm.Server.Admin;

/// <summary>
/// /api 下的管理接口。除了登录相关的几个，全部要求已登录，而且生成的密码已经改掉（Q349）。
/// </summary>
internal static class AdminApi
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/api");
        api.MapGroup("/auth").MapAuthEndpoints();
        api.MapGroup("/channels").RequireAuthorization().MapChannelEndpoints().MapChannelRestrictionEndpoints().MapChannelRewriteEndpoints().MapChannelDebugEndpoints();
        api.MapGroup("/models").RequireAuthorization().MapModelEndpoints();
        api.MapGroup("/api-keys").RequireAuthorization().MapApiKeyEndpoints().MapApiKeyRestrictionEndpoints().MapApiKeyDebugEndpoints();
        api.MapGroup("/requests").RequireAuthorization().MapRequestEndpoints().MapRequestDebugEndpoints();
        api.MapGroup("/price-catalog").RequireAuthorization().MapPriceCatalogEndpoints();
        api.MapGroup("/prices").RequireAuthorization().MapPriceEndpoints();
        api.MapGroup("/stats").RequireAuthorization().MapStatsEndpoints();
        api.MapGroup("/settings").RequireAuthorization().MapSettingsEndpoints().MapDebugSettingsEndpoints();
        api.MapGroup("/live").RequireAuthorization().MapLiveEndpoints();
        api.MapGroup("/model-sync-events").RequireAuthorization().MapModelSyncEventEndpoints();
        api.MapGroup("/workbench").RequireAuthorization().MapWorkbenchEndpoints();
        api.MapGroup("/audit").RequireAuthorization().MapAuditEndpoints();
        return endpoints;
    }
}
