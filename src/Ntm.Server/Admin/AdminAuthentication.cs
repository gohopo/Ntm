using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Ntm.Gateway.Security;

namespace Ntm.Server.Admin;

/// <summary>
/// 单管理员的 cookie 会话。
/// cookie 只挂在 /api 下、SameSite=Strict、脚本读不到；
/// 里面带着会话版本号，改密码或重置之后版本号变了，旧 cookie 立刻失效。
/// </summary>
internal static class AdminAuthentication
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string CookieName = "ntm_session";

    /// <summary>登录和改密码共用的限流策略。</summary>
    public const string LoginRateLimitPolicy = "admin-login";

    /// <summary>每个来源地址每分钟最多试几次密码。</summary>
    public const int LoginAttemptsPerMinute = 5;

    public const string AdminName = "admin";

    private const string SessionVersionClaim = "ntm:sv";

    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            // 只有管理接口需要它；静态文件和 /v1 的请求都不带这个 cookie。
            options.Cookie.Path = "/api";
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;

            // 这些是给前端调的 JSON 接口：不跳登录页，直接回状态码。
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = ValidateSessionAsync;
        });

        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return new ValueTask(TypedResults.Problem(
                    title: "试得太频繁了，请一分钟后再试。",
                    statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext));
            };

            // 按来源地址分桶。部署在反向代理后面时要打开转发头，否则所有人共用代理的地址。
            options.AddPolicy(LoginRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = LoginAttemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    public static Task SignInAsync(HttpContext context, int sessionVersion)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, AdminName),
                new Claim(SessionVersionClaim, sessionVersion.ToString(CultureInfo.InvariantCulture)),
            ],
            Scheme);

        return context.SignInAsync(
            Scheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static Task SignOutAsync(HttpContext context) => context.SignOutAsync(Scheme);

    /// <summary>每次带 cookie 的请求都对一下会话版本，改过密码之前签发的会话就此作废。</summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        string? claimed = context.Principal?.FindFirst(SessionVersionClaim)?.Value;

        AdminAccountService accounts = context.HttpContext.RequestServices.GetRequiredService<AdminAccountService>();
        int? current = await accounts.GetSessionVersionAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);

        if (current is null
            || !int.TryParse(claimed, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version != current.Value)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme).ConfigureAwait(false);
        }
    }
}
