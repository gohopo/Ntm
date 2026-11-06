using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Debugging;

/// <summary>
/// 调试原文的清理（Q159）：分批删掉超过保留期的，再用 incremental_vacuum 把空出来的页还回去；
/// 顺手把已经到期的开关清成 null（Q155）。到期判断本来就按时间戳，这一步只是让库里的状态干净。
/// </summary>
public sealed partial class DebugCleaner(
    IServiceScopeFactory scopes,
    IOptions<DebugCaptureOptions> options,
    TimeProvider timeProvider,
    ILogger<DebugCleaner> logger) : BackgroundService
{
    private const int DeleteBatchSize = 200;

    /// <summary>删掉过期的原文，返回删了几份。</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        DebugCaptureOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        long cutoff = now.AddHours(-Math.Max(1, settings.RetentionHours)).ToUnixTimeMilliseconds();
        int total = 0;

        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            DebugDbContext db = scope.ServiceProvider.GetRequiredService<DebugDbContext>();

            while (!cancellationToken.IsCancellationRequested)
            {
                // CapturedAt 存成 Unix 毫秒，按原始列比较，避免值转换器在 Where 里翻译不了。
                List<long> ids = await db.DebugPayloads
                    .FromSqlInterpolated($"SELECT * FROM debug_payloads WHERE CapturedAt < {cutoff} ORDER BY Id LIMIT {DeleteBatchSize}")
                    .Select(p => p.Id)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (ids.Count == 0)
                {
                    break;
                }

                await db.DebugPayloads
                    .Where(p => ids.Contains(p.Id))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                total += ids.Count;
                if (ids.Count < DeleteBatchSize)
                {
                    break;
                }
            }

            if (total > 0)
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken).ConfigureAwait(false);
                LogDeleted(logger, total, null);
            }
        }

        await ClearExpiredSwitchesAsync(now, cancellationToken).ConfigureAwait(false);
        return total;
    }

    private async Task ClearExpiredSwitchesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        // 列是 Unix 毫秒；同上，用原始 SQL 比较。
        long at = now.ToUnixTimeMilliseconds();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE settings SET DebugCaptureUntil = NULL WHERE DebugCaptureUntil IS NOT NULL AND DebugCaptureUntil <= {at}",
            cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE channels SET DebugCaptureUntil = NULL WHERE DebugCaptureUntil IS NOT NULL AND DebugCaptureUntil <= {at}",
            cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE api_keys SET DebugCaptureUntil = NULL WHERE DebugCaptureUntil IS NOT NULL AND DebugCaptureUntil <= {at}",
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, timeProvider);
        do
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // 清理失败只记日志，下一轮再试；不能让后台任务停掉。
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1211, Level = LogLevel.Information, Message = "删掉了 {Count} 份过期的调试原文。")]
    private static partial void LogDeleted(ILogger logger, int count, Exception? exception);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Warning, Message = "清理调试原文失败，下一轮再试。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
