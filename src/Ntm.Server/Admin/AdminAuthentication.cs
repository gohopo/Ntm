using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Ntm.Gateway.Security;

namespace Ntm.Server.Admin;

/// <summary>
/// 单管理员的 cookie 会话。
/// cookie 只挂在 /api 下、SameSite=Strict、脚本读不到；
/// 里面带着会话版本号，改密码、重置或「退出所有设备」之后版本号变了，旧 cookie 立刻失效。
/// 7 天不用就过期；不管用不用，登录满 30 天也要重新登录（Q354）。
/// 还在用网关生成的密码时，只放行会话相关的几个接口，其余一律 403（Q349）。
/// </summary>
internal static class AdminAuthentication
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string CookieName = "ntm_session";

    /// <summary>只要求登录、不要求先改掉生成的密码：/api/auth 下的会话接口用它。</summary>
    public const string SessionPolicy = "admin-session";

    public const string AdminName = "admin";

    /// <summary>登录满这么久必须重新登录，滑动续期也不能越过它（Q354）。</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    private const string SessionVersionClaim = "ntm:sv";

    /// <summary>这次登录的时间（Unix 毫秒）。续期时 cookie 里的声明原样保留，所以它一直是登录那一刻。</summary>
    private const string IssuedAtClaim = "ntm:iat";

    private const string MustChangePasswordItem = "ntm:must-change-password";

    /// <summary>403 回应里的 <c>code</c>，前端看到它就跳去改密码。</summary>
    public const string MustChangePasswordCode = "must-change-password";

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

        services.AddSingleton<IAuthorizationHandler, PasswordChangedHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, MustChangePasswordResultHandler>();
        services.AddAuthorization(options =>
        {
            // 默认策略（RequireAuthorization() 不带参数时用的）多一条：生成的密码已经改掉。
            options.DefaultPolicy = new AuthorizationPolicyBuilder(Scheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new PasswordChangedRequirement())
                .Build();
            options.AddPolicy(SessionPolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        });

        services.AddSingleton<LoginThrottle>();

        return services;
    }

    /// <summary>签发新 cookie。登录和改密码都算重新验证过身份，30 天从这一刻重新算。</summary>
    public static Task SignInAsync(HttpContext context, int sessionVersion)
    {
        long issuedAt = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds();
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, AdminName),
                new Claim(SessionVersionClaim, sessionVersion.ToString(CultureInfo.InvariantCulture)),
                new Claim(IssuedAtClaim, issuedAt.ToString(CultureInfo.InvariantCulture)),
            ],
            Scheme);

        return context.SignInAsync(
            Scheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static Task SignOutAsync(HttpContext context) => context.SignOutAsync(Scheme);

    /// <summary>这个请求的会话是否还在用生成的密码。只有通过了会话校验的请求才有这个标记。</summary>
    public static bool MustChangePassword(HttpContext context) =>
        context.Items.TryGetValue(MustChangePasswordItem, out object? value) && value is true;

    /// <summary>
    /// 每次带 cookie 的请求都对一下会话版本，改过密码之前签发的会话就此作废；
    /// 再看登录时间，满 30 天的也作废。顺手记下要不要先改密码，给授权那一步用。
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        string? claimedVersion = context.Principal?.FindFirst(SessionVersionClaim)?.Value;
        string? claimedIssuedAt = context.Principal?.FindFirst(IssuedAtClaim)?.Value;

        AdminAccountService accounts = context.HttpContext.RequestServices.GetRequiredService<AdminAccountService>();
        AdminSessionState? state = await accounts.GetSessionStateAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        DateTimeOffset now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

        // 没有登录时间的是升级前签发的 cookie，当成过期：重新登录一次就有了。
        bool valid = state is not null
            && int.TryParse(claimedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            && version == state.Value.SessionVersion
            && long.TryParse(claimedIssuedAt, NumberStyles.None, CultureInfo.InvariantCulture, out long issuedAt)
            && issuedAt <= now.ToUnixTimeMilliseconds()
            && issuedAt > now.Subtract(AbsoluteLifetime).ToUnixTimeMilliseconds();

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme).ConfigureAwait(false);
            return;
        }

        context.HttpContext.Items[MustChangePasswordItem] = state!.Value.MustChangePassword;
    }

    private sealed class PasswordChangedRequirement : IAuthorizationRequirement;

    private sealed class PasswordChangedHandler : AuthorizationHandler<PasswordChangedRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PasswordChangedRequirement requirement)
        {
            // 拿不到 HttpContext 或者没有标记时一律不放行：宁可多挡，不要漏放。
            if (context.Resource is HttpContext http
                && http.Items.TryGetValue(MustChangePasswordItem, out object? value)
                && value is false)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>因为没改生成的密码被挡时，回一个说得清原因的 403，前端据此跳到改密码页。</summary>
    private sealed class MustChangePasswordResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden
                && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PasswordChangedRequirement>().Any() == true)
            {
                await TypedResults.Problem(
                    title: "请先修改密码。现在的密码是网关生成的，出现在日志或终端里。",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = MustChangePasswordCode })
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
        }
    }
}
