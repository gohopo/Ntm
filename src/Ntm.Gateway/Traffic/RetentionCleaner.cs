using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 每小时删一次过了保留期的请求记录（Q42）。小事务分批删，写入器不会被长时间挡住。
/// 汇总不删：它是长期的账。
/// </summary>
public sealed partial class RetentionCleaner(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>删掉开始时间早于保留期的请求记录与它们的 Attempt，返回删了多少条请求。</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        RetentionOptions settings = options.Value;
        if (settings.RetentionDays <= 0)
        {
            return 0;
        }

        DateTimeOffset cutoff = timeProvider.GetUtcNow().AddDays(-settings.RetentionDays);
        int batchSize = Math.Max(1, settings.DeleteBatchSize);
        int total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();

            List<Guid> ids = await db.Requests
                .Where(r => r.StartedAt < cutoff)
                .OrderBy(r => r.StartedAt)
                .Select(r => r.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                break;
            }

            await using IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Attempts
                .Where(a => ids.Contains(a.RequestId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await db.Requests
                .Where(r => ids.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            total += ids.Count;
            if (ids.Count < batchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            LogDeleted(logger, total, settings.RetentionDays, null);
        }

        return total;
    }

    /// <summary>删掉过了保留期或超出上限的 ResponseLanding，返回删了多少行（Q285）。</summary>
    public async Task<int> DeleteExpiredLandingsAsync(CancellationToken cancellationToken)
    {
        RetentionOptions settings = options.Value;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();

        int total = 0;
        if (settings.LandingRetentionDays > 0)
        {
            DateTimeOffset cutoff = timeProvider.GetUtcNow().AddDays(-settings.LandingRetentionDays);
            total += await db.ResponseLandings
                .Where(l => l.LastUsedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (settings.MaxLandings > 0)
        {
            int excess = await db.ResponseLandings.CountAsync(cancellationToken).ConfigureAwait(false) - settings.MaxLandings;
            while (excess > 0 && !cancellationToken.IsCancellationRequested)
            {
                int take = Math.Min(excess, Math.Max(1, settings.DeleteBatchSize));
                List<string> oldest = await db.ResponseLandings
                    .OrderBy(l => l.LastUsedAt)
                    .Select(l => l.Id)
                    .Take(take)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (oldest.Count == 0)
                {
                    break;
                }

                total += await db.ResponseLandings
                    .Where(l => oldest.Contains(l.Id))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                excess -= oldest.Count;
            }
        }

        if (total > 0)
        {
            LogLandingsDeleted(logger, total, null);
        }

        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken).ConfigureAwait(false);
                await DeleteExpiredLandingsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // 清理失败只记日志，下个小时再试；不能让后台任务停掉。
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

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "删掉了 {Count} 条超过 {Days} 天的请求记录。")]
    private static partial void LogDeleted(ILogger logger, int count, int days, Exception? exception);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Information, Message = "删掉了 {Count} 条过期或超出上限的响应落点。")]
    private static partial void LogLandingsDeleted(ILogger logger, int count, Exception? exception);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "清理过期请求记录失败，一小时后再试。")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
