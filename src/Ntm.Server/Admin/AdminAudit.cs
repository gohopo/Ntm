using Ntm.Gateway.Audit;

namespace Ntm.Server.Admin;

/// <summary>
/// 管理接口上改数据的请求成功后写一条操作记录（Q358）。请求期间开一个 <see cref="AuditScope"/>，
/// 配置库每次保存都把改了哪些行、哪些字段记进去；只记字段名，不记值。
/// 不记的：登录这几个（<see cref="AuthEndpoints"/> 自己记）、工作台发送（已经在请求记录里，来源是工作台）、
/// 只读的预览。失败的请求不记：没改成什么。
/// </summary>
internal static class AdminAudit
{
    private static readonly string[] SkippedPrefixes = ["/api/auth", "/api/workbench"];

    public static IApplicationBuilder UseNtmAdminAudit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!ShouldRecord(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            using AuditScope scope = AuditScope.Begin();
            await next(context).ConfigureAwait(false);
            IReadOnlyList<AuditChange> changes = scope.Complete();

            if (context.Response.StatusCode >= 400)
            {
                return;
            }

            AuditEntry entry = Describe(context, changes);
            AuditWriter writer = context.RequestServices.GetRequiredService<AuditWriter>();

            // 请求已经成功了，客户端断开也要记下来。
            await writer.WriteAsync(entry, CancellationToken.None).ConfigureAwait(false);
        });

    private static bool ShouldRecord(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || SkippedPrefixes.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 没匹配上接口的（404）和预览这种只读的 POST 都不记。
        return context.GetEndpoint() is RouteEndpoint endpoint
            && !endpoint.RoutePattern.RawText!.EndsWith("/preview", StringComparison.Ordinal);
    }

    private static AuditEntry Describe(HttpContext context, IReadOnlyList<AuditChange> changes)
    {
        var endpoint = (RouteEndpoint)context.GetEndpoint()!;
        string template = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');

        // 路由约束（{id:guid}）对看记录的人没用。
        template = System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+):[^}]+\}", "{$1}");

        string[] segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? targetType = segments.Length > 1 ? segments[1] : null;
        string? targetId = context.Request.RouteValues.TryGetValue("id", out object? id) ? id?.ToString() : null;

        bool channelKey = targetType == "channels" && segments.Length > 3 && segments[3] == "keys";
        if (channelKey)
        {
            targetType = "channel-keys";
            targetId = context.Request.RouteValues.TryGetValue("keyId", out object? keyId) ? keyId?.ToString() : null;
        }

        string? entity = targetType switch
        {
            "channels" => "Channel",
            "channel-keys" => "ChannelKey",
            "api-keys" => "ApiKey",
            "models" => "PublishedModel",
            "prices" => "PriceRow",
            "settings" => "GatewaySettings",
            _ => null,
        };
        // 不同实体的数字 id 可以相同，先匹配类型；嵌套渠道密钥不是父渠道。
        AuditChange? primary = changes.FirstOrDefault(c =>
            (entity is null || c.Entity == entity)
            && (targetId is null ? c.State == "added" : c.EntityId == targetId));
        targetId ??= primary?.EntityId;

        return new AuditEntry
        {
            Action = $"{context.Request.Method} {template}",
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
            TargetType = targetType,
            TargetId = targetId,
            TargetName = primary?.Name,
            Changes = [.. changes],
        };
    }
}
