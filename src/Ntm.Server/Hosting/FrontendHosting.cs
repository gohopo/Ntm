using Microsoft.Extensions.FileProviders;
using Ntm.Gateway.Relay;

namespace Ntm.Server.Hosting;

/// <summary>
/// 管理前端是 SvelteKit 构建出的静态 SPA，放在 wwwroot。
/// /v1 和 /api 下没对上的路径各自按自己的格式回 404，拿不到一张 HTML 页面——
/// API 客户端不该为了判断出错去解析 HTML。其余不带扩展名的路径交给 SPA 的入口页，由前端路由接手。
/// </summary>
internal static class FrontendHosting
{
    public const string EntryFile = "200.html";

    public static WebApplication MapNtmFrontend(this WebApplication app)
    {
        app.MapFallback("/v1/{**path}", WriteRelayNotFoundAsync);
        app.MapFallback(
            "/api/{**path}",
            () => TypedResults.Problem(title: "没有这个接口。", statusCode: StatusCodes.Status404NotFound));

        // MapStaticAssets 读的是构建时生成的清单（带压缩版本和指纹缓存）；
        // 清单不在时只靠 UseStaticFiles 兜底，照样能用，只是少了这些优化。
        string manifest = Path.Combine(
            AppContext.BaseDirectory,
            app.Environment.ApplicationName + ".staticwebassets.endpoints.json");
        if (File.Exists(manifest))
        {
            app.MapStaticAssets();
        }

        IFileInfo entry = app.Environment.WebRootFileProvider.GetFileInfo(EntryFile);
        if (entry.Exists)
        {
            app.MapFallbackToFile(EntryFile, new StaticFileOptions
            {
                // 入口页不能缓存，否则发版之后浏览器还拿着旧页面去要已经不存在的脚本。
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        else
        {
            app.MapFallback(() => TypedResults.Text(
                "管理页面没有构建。先在仓库根目录运行 pnpm build:web，再构建 Ntm.Server。",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status404NotFound));
        }

        return app;
    }

    /// <summary>/v1 下的未知路径按客户端的格式回 404：带 x-api-key 的当作 Anthropic 客户端。</summary>
    private static async Task WriteRelayNotFoundAsync(HttpContext context)
    {
        InboundDialect dialect = context.Request.Headers.ContainsKey("x-api-key")
            ? InboundDialect.Anthropic
            : InboundDialect.OpenAiChat;

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        byte[] body = dialect.ErrorBody(GatewayErrorKind.InvalidRequest, "没有这个接口。");
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
