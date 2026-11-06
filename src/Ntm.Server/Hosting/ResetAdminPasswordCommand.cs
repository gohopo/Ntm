using Ntm.Gateway;
using Ntm.Gateway.Data;
using Ntm.Gateway.Security;

namespace Ntm.Server.Hosting;

/// <summary>
/// <c>reset-admin-password</c>：生成一个新的随机密码、只打印这一次，并让所有已登录的会话失效。
/// 故意不接受从参数传入密码——那样会留在 shell 历史和进程列表里。
/// </summary>
internal static class ResetAdminPasswordCommand
{
    public const string Name = "reset-admin-password";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        // 和正常启动读同一套配置（appsettings、环境变量、命令行），数据目录才对得上。
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddNtmGateway(builder.Configuration);
        await using WebApplication app = builder.Build();

        await app.Services.MigrateNtmDatabasesAsync(cancellationToken).ConfigureAwait(false);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        string password = await scope.ServiceProvider.GetRequiredService<AdminAccountService>()
            .ResetAsync(cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync("管理员密码已重置，所有已登录的会话都已失效。新密码（只显示这一次）：")
            .ConfigureAwait(false);
        await output.WriteLineAsync(password).ConfigureAwait(false);
        return 0;
    }
}
