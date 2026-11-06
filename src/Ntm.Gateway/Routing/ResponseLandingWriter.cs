using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 把 ResponseLanding 的改动写进 response_landings（Q269）。一批里同一个 id 只留最后一次改动；
/// 停机时把队列剩下的刷完。写失败只记日志：内存里的落点还在，丢的只是重启后的那部分。
/// </summary>
public sealed partial class ResponseLandingWriter(
    ResponseLandingTable table,
    IServiceScopeFactory scopes,
    ILogger<ResponseLandingWriter> logger) : BackgroundService
{
    public const int MaxBatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ResponseLandingWrite>();
        try
        {
            while (await table.Writes.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                Drain(batch);
                await PersistSafelyAsync(batch, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // 停机：队列里剩的照样写完，不带取消。
        table.CompleteWrites();
        Drain(batch);
        await PersistSafelyAsync(batch, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>测试用：把队列里现有的改动立刻写完。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var batch = new List<ResponseLandingWrite>();
        Drain(batch);
        await PersistAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private void Drain(List<ResponseLandingWrite> batch)
    {
        while (batch.Count < MaxBatchSize && table.Writes.TryRead(out ResponseLandingWrite write))
        {
            batch.Add(write);
        }
    }

    private async Task PersistSafelyAsync(List<ResponseLandingWrite> batch, CancellationToken cancellationToken)
    {
        while (batch.Count > 0)
        {
            try
            {
                await PersistAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // 写不进去只记日志：内存里的落点还能用，后台任务不能停。
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex, batch.Count);
                batch.Clear();
            }

            Drain(batch);
        }
    }

    private async Task PersistAsync(List<ResponseLandingWrite> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var latest = new Dictionary<string, ResponseLanding?>(StringComparer.Ordinal);
        foreach (ResponseLandingWrite write in batch)
        {
            latest[write.Id] = write.Landing;
        }

        batch.Clear();

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        List<string> ids = [.. latest.Keys];
        Dictionary<string, ResponseLanding> existing = await db.ResponseLandings
            .Where(l => ids.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        foreach ((string id, ResponseLanding? landing) in latest)
        {
            bool found = existing.TryGetValue(id, out ResponseLanding? row);
            if (landing is null)
            {
                if (found)
                {
                    db.ResponseLandings.Remove(row!);
                }
            }
            else if (found)
            {
                db.Entry(row!).CurrentValues.SetValues(landing);
            }
            else
            {
                db.ResponseLandings.Add(landing);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach ((string id, ResponseLanding? landing) in latest)
        {
            if (landing is null)
            {
                table.DeletePersisted(id);
            }
        }
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "写入 {Count} 条响应落点失败；内存里的还能用，重启后会丢。")]
    private static partial void LogFailed(ILogger logger, Exception exception, int count);
}
