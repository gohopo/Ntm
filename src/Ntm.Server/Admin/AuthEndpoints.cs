using System.Globalization;
using Ntm.Gateway.Security;

namespace Ntm.Server.Admin;

internal sealed record LoginInput(string? Password);

internal sealed record PasswordChangeInput(string? CurrentPassword, string? NewPassword);

internal sealed record AdminView(string Name);

/// <summary>登录、登出、当前会话和改密码。</summary>
internal static partial class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", LoginAsync).RequireRateLimiting(AdminAuthentication.LoginRateLimitPolicy);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", Me).RequireAuthorization();
        group.MapPost("/password", ChangePasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting(AdminAuthentication.LoginRateLimitPolicy);
        return group;
    }

    private static async Task<IResult> LoginAsync(
        LoginInput input,
        HttpContext context,
        AdminAccountService accounts,
        ILoggerFactory loggers)
    {
        if (string.IsNullOrEmpty(input.Password))
        {
            return new ValidationErrors().Add("password", "请输入密码。").ToResult();
        }

        int? version = await accounts.VerifyAsync(input.Password, context.RequestAborted).ConfigureAwait(false);
        if (version is null)
        {
            LogLoginFailed(
                loggers.CreateLogger("Ntm.Server.Admin"),
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            // 用 400 不用 401：前端把 401 当成「会话没了，回登录页」，登录失败本身不该触发它。
            return TypedResults.Problem(title: "密码不对。", statusCode: StatusCodes.Status400BadRequest);
        }

        await AdminAuthentication.SignInAsync(context, version.Value).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>没登录也能调，重复调用无害。签名就是 RequestDelegate，状态码自己写。</summary>
    private static async Task LogoutAsync(HttpContext context)
    {
        await AdminAuthentication.SignOutAsync(context).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static AdminView Me() => new(AdminAuthentication.AdminName);

    private static async Task<IResult> ChangePasswordAsync(
        PasswordChangeInput input,
        HttpContext context,
        AdminAccountService accounts)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(input.CurrentPassword))
        {
            errors.Add("currentPassword", "请输入当前密码。");
        }

        if (string.IsNullOrEmpty(input.NewPassword))
        {
            errors.Add("newPassword", "请输入新密码。");
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        PasswordChangeResult result = await accounts
            .ChangePasswordAsync(input.CurrentPassword!, input.NewPassword!, context.RequestAborted)
            .ConfigureAwait(false);

        if (result.Status == PasswordChangeStatus.WrongCurrentPassword)
        {
            return errors.Add("currentPassword", "当前密码不对。").ToResult();
        }

        if (result.Status == PasswordChangeStatus.TooShort)
        {
            return errors.Add(
                    "newPassword",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"新密码至少要 {AdminAccountService.MinimumPasswordLength} 个字符。"))
                .ToResult();
        }

        // 版本号已经变了，其他会话随之失效；当前这个会话换一张新 cookie 继续用。
        await AdminAuthentication.SignInAsync(context, result.SessionVersion).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "管理员登录失败，来源 {RemoteAddress}。")]
    private static partial void LogLoginFailed(ILogger logger, string remoteAddress);
}
