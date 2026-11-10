using System.Globalization;
using Ntm.Gateway.Audit;
using Ntm.Gateway.Security;

namespace Ntm.Server.Admin;

internal sealed record LoginInput(string? Password);

internal sealed record PasswordChangeInput(string? CurrentPassword, string? NewPassword);

/// <summary>当前会话。<paramref name="MustChangePassword"/> 为 true 时前端只给改密码页（Q349）。</summary>
internal sealed record AdminView(string Name, bool MustChangePassword);

/// <summary>
/// 登录、登出、当前会话、改密码和「退出所有设备」。这几个接口自己写操作记录（Q358），
/// 管理接口那层通用的操作记录跳过 /api/auth。
/// </summary>
internal static partial class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", LoginAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", Me).RequireAuthorization(AdminAuthentication.SessionPolicy);
        group.MapPost("/password", ChangePasswordAsync).RequireAuthorization(AdminAuthentication.SessionPolicy);
        group.MapPost("/logout-all", LogoutAllAsync).RequireAuthorization(AdminAuthentication.SessionPolicy);
        return group;
    }

    private static async Task<IResult> LoginAsync(
        LoginInput input,
        HttpContext context,
        AdminAccountService accounts,
        LoginThrottle throttle,
        AuditWriter audit,
        ILoggerFactory loggers)
    {
        using IDisposable check = await throttle.EnterAsync(context.RequestAborted).ConfigureAwait(false);
        if (Throttled(context, throttle) is { } limited)
        {
            return limited;
        }

        if (string.IsNullOrEmpty(input.Password))
        {
            return new ValidationErrors().Add("password", "请输入密码。").ToResult();
        }

        ILogger logger = loggers.CreateLogger("Ntm.Server.Admin");
        string remote = RemoteAddress(context);
        int? version = await accounts.VerifyAsync(input.Password, context.RequestAborted).ConfigureAwait(false);
        if (version is null)
        {
            throttle.RecordFailure(context.Connection.RemoteIpAddress);
            LogLoginFailed(logger, remote);
            await audit.WriteAsync(Entry(context, AuditActions.Login, succeeded: false), CancellationToken.None).ConfigureAwait(false);

            // 用 400 不用 401：前端把 401 当成「会话没了，回登录页」，登录失败本身不该触发它。
            return TypedResults.Problem(title: "密码不对。", statusCode: StatusCodes.Status400BadRequest);
        }

        await AdminAuthentication.SignInAsync(context, version.Value).ConfigureAwait(false);
        LogLoginSucceeded(logger, remote);
        // 已完成的认证操作即使客户端断开也必须留下操作记录。
        await audit.WriteAsync(Entry(context, AuditActions.Login), CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>没登录也能调，重复调用无害。只有真的结束了一个会话才记操作记录。</summary>
    private static async Task<IResult> LogoutAsync(HttpContext context, AuditWriter audit)
    {
        bool signedIn = context.User.Identity?.IsAuthenticated == true;
        await AdminAuthentication.SignOutAsync(context).ConfigureAwait(false);
        if (signedIn)
        {
            await audit.WriteAsync(Entry(context, AuditActions.Logout), CancellationToken.None).ConfigureAwait(false);
        }

        return TypedResults.NoContent();
    }

    private static AdminView Me(HttpContext context) =>
        new(AdminAuthentication.AdminName, AdminAuthentication.MustChangePassword(context));

    /// <summary>所有设备上的会话都作废，包括现在这个（Q354）。和改密码一样靠会话版本号，只是密码不变。</summary>
    private static async Task<IResult> LogoutAllAsync(HttpContext context, AdminAccountService accounts, AuditWriter audit)
    {
        await accounts.RevokeSessionsAsync(context.RequestAborted).ConfigureAwait(false);
        await AdminAuthentication.SignOutAsync(context).ConfigureAwait(false);
        await audit.WriteAsync(Entry(context, AuditActions.LogoutAll), CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        PasswordChangeInput input,
        HttpContext context,
        AdminAccountService accounts,
        LoginThrottle throttle,
        AuditWriter audit)
    {
        // 改密码要验当前密码，等于又一个猜密码的入口，和登录共用一个限流。
        using IDisposable check = await throttle.EnterAsync(context.RequestAborted).ConfigureAwait(false);
        if (Throttled(context, throttle) is { } limited)
        {
            return limited;
        }

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
            throttle.RecordFailure(context.Connection.RemoteIpAddress);
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

        if (result.Status == PasswordChangeStatus.UnchangedGeneratedPassword)
        {
            return errors.Add("newPassword", "新密码不能继续使用日志或终端里的生成密码。").ToResult();
        }

        // 版本号已经变了，其他会话随之失效；当前这个会话换一张新 cookie 继续用。
        await AdminAuthentication.SignInAsync(context, result.SessionVersion).ConfigureAwait(false);
        await audit.WriteAsync(Entry(context, AuditActions.PasswordChanged), CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? Throttled(HttpContext context, LoginThrottle throttle)
    {
        if (throttle.RetryAfter(context.Connection.RemoteIpAddress) is not { } wait)
        {
            return null;
        }

        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            title: string.Create(CultureInfo.InvariantCulture, $"密码试错太多次，{seconds} 秒后再试。"),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static string RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static AuditEntry Entry(HttpContext context, string action, bool succeeded = true) => new()
    {
        Action = action,
        Succeeded = succeeded,
        RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
        TargetType = "admin",
        TargetId = AdminAccount.SingletonId.ToString(CultureInfo.InvariantCulture),
        TargetName = AdminAuthentication.AdminName,
        Changes = action switch
        {
            AuditActions.PasswordChanged => [new AuditChange
            {
                Entity = nameof(AdminAccount),
                EntityId = AdminAccount.SingletonId.ToString(CultureInfo.InvariantCulture),
                State = "modified",
                Fields = "Password,SessionVersion,MustChangePassword",
            }],
            AuditActions.LogoutAll => [new AuditChange
            {
                Entity = nameof(AdminAccount),
                EntityId = AdminAccount.SingletonId.ToString(CultureInfo.InvariantCulture),
                State = "modified",
                Fields = nameof(AdminAccount.SessionVersion),
            }],
            _ => [],
        },
    };

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "管理员登录失败，来源 {RemoteAddress}。")]
    private static partial void LogLoginFailed(ILogger logger, string remoteAddress);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "管理员登录成功，来源 {RemoteAddress}。")]
    private static partial void LogLoginSucceeded(ILogger logger, string remoteAddress);
}
