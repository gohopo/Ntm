using Microsoft.AspNetCore.Http.Features;
using System.Net.Http.Headers;

namespace Ntm.Server.Admin;

/// <summary>
/// 挡跨站请求（Q351，ADR-0023）。管理接口靠 cookie 认人，别的网站可以让管理员的浏览器带着 cookie 发请求，
/// 所以 /api 下改数据的请求（GET、HEAD、OPTIONS 以外）要先过这一关：
/// <list type="number">
/// <item>有 <c>Sec-Fetch-Site</c>（现代浏览器都会带）：只放行 <c>same-origin</c> 和 <c>none</c>（地址栏、书签）。
/// <c>same-site</c> 也挡：同一个主域下的别的子域不一定可信。</item>
/// <item>没有它但有 <c>Origin</c>：Origin 的主机和端口必须和请求的 Host 一样。</item>
/// <item>两个都没有：为了兼容 curl、脚本而放行；旧浏览器也可能省略这两个头，所以还保留 SameSite=Strict 和 JSON 限制。</item>
/// <item>带请求体的必须是 <c>application/json</c>，否则 415。HTML 表单发不出 JSON，这是第二道保险。</item>
/// </list>
/// 不用 ASP.NET 的防伪令牌：它只检查表单绑定的接口，这里全是 JSON 接口，用了还得多一套取令牌的流程。
/// </summary>
internal static partial class CrossSiteGuard
{
    public static IApplicationBuilder UseNtmCrossSiteGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            HttpRequest request = context.Request;
            if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || HttpMethods.IsGet(request.Method)
                || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!IsSameOrigin(request, out string? reason))
            {
                LogRejected(
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Ntm.Server.Admin"),
                    request.Method,
                    request.Path.Value ?? "",
                    reason!,
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                await TypedResults.Problem(
                    title: "跨站请求被拒绝。请直接在南天门的管理页面里操作。",
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            if (HasBody(request) && !IsJson(request.ContentType))
            {
                await TypedResults.Problem(
                    title: "请求体必须是 JSON（Content-Type: application/json）。",
                    statusCode: StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

    internal static bool IsSameOrigin(HttpRequest request, out string? reason)
    {
        string? fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (request.Headers.ContainsKey("Sec-Fetch-Site"))
        {
            reason = $"Sec-Fetch-Site: {fetchSite}";
            return fetchSite is "same-origin" or "none";
        }

        string? origin = request.Headers.Origin.ToString();
        if (request.Headers.ContainsKey("Origin"))
        {
            reason = $"Origin: {origin}";

            // 隐私模式等场合浏览器会发 "null"，这时说不清来源，按跨站处理。
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https") || !request.Host.HasValue)
            {
                return false;
            }

            HostString host = request.Host;
            if (!string.Equals(uri.Host.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 使用可信代理处理后的协议来归一化默认端口；没配置代理信任不能放宽同源边界。
            int effectivePort = host.Port ?? (request.IsHttps ? 443 : 80);
            return uri.Port == effectivePort;
        }

        reason = null;
        return true;
    }

    private static bool HasBody(HttpRequest request) =>
        request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
        ?? (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0);

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
        && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "拒绝了跨站请求 {Method} {Path}（{Reason}），来源 {RemoteAddress}。")]
    private static partial void LogRejected(ILogger logger, string method, string path, string reason, string remoteAddress);
}
