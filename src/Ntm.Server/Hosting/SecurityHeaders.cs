namespace Ntm.Server.Hosting;

internal static class SecurityHeaders
{
    /// <summary>
    /// 给管理页面和 /api 加几条基本的安全响应头。
    /// /v1 是转发给 API 客户端的通道，响应头按转发的约定来，这里不碰。
    /// </summary>
    public static IApplicationBuilder UseNtmSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";

                // 页面里的 CSP 是 meta 标签（SvelteKit 生成，Q356），而 meta 里的 frame-ancestors 不生效，只能放在响应头里。
                // 老浏览器认 X-Frame-Options，新浏览器认这个。
                headers.ContentSecurityPolicy = "frame-ancestors 'none'";
                headers["Referrer-Policy"] = "no-referrer";

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    // 管理接口的回应里可能有刚生成的 API 密钥原文，不能让浏览器或中间的代理缓存下来。
                    headers.CacheControl = "no-store";
                }
            }

            await next(context).ConfigureAwait(false);
        });
}
