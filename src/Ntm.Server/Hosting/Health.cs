using System.Data.Common;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Data;

namespace Ntm.Server.Hosting;

/// <summary>健康检查要看的东西。测试里可以换掉，模拟库坏了。</summary>
internal interface IHealthProbe
{
    /// <summary>返回出问题的部分的名字；全好时是空的。</summary>
    Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>配置库和请求记录库各跑一次 <c>SELECT 1</c>，2 秒内回来才算好（Q338）。调试库坏了不影响转发，不查。</summary>
internal sealed class DatabaseHealthProbe(IServiceScopeFactory scopes) : IHealthProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        var failed = new List<string>();
        if (!await PingAsync(scope.ServiceProvider.GetRequiredService<ConfigDbContext>(), deadline.Token).ConfigureAwait(false))
        {
            failed.Add("配置库");
        }

        if (!await PingAsync(scope.ServiceProvider.GetRequiredService<TrafficDbContext>(), deadline.Token).ConfigureAwait(false))
        {
            failed.Add("请求记录库");
        }

        return failed;
    }

    private static async Task<bool> PingAsync(DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed record HealthView(
    string Status,
    string Version,
    string? Commit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Failed = null);

internal static class HealthEndpoint
{
    /// <summary>不要求登录：负载均衡器和容器的健康检查用它。</summary>
    public static async Task<IResult> HandleAsync(IHealthProbe probe, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> failed = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        return failed.Count == 0
            ? Results.Ok(new HealthView("ok", BuildInfo.Version, BuildInfo.Commit))
            : Results.Json(
                new HealthView("unhealthy", BuildInfo.Version, BuildInfo.Commit, failed),
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
