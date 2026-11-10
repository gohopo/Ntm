using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Audit;

/// <summary>
/// 写操作记录（Q358）。管理操作一分钟顶多几条，直接同步写进请求记录库，不走请求记录那条批量写入队列。
/// 写失败只记日志：不能因为记不下来就让已经生效的改动报错。
/// </summary>
public sealed partial class AuditWriter(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<AuditWriter> logger)
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OccurredAt == default)
        {
            entry.OccurredAt = timeProvider.GetUtcNow();
        }

        if (entry.Action.Length > AuditEntry.MaxActionLength)
        {
            entry.Action = entry.Action[..AuditEntry.MaxActionLength];
        }

        if (entry.TargetName is { Length: > AuditEntry.MaxNameLength } name)
        {
            entry.TargetName = name[..AuditEntry.MaxNameLength];
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            db.AuditEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 见类注释：记录失败不影响操作本身。
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, entry.Action, ex);
        }
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Error, Message = "操作记录没写进去：{Action}。")]
    private static partial void LogFailed(ILogger logger, string action, Exception exception);
}
