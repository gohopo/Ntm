using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Data;
using Ntm.Protocol;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 单个后台写入器：攒够一批或到点就写一次（ADR-0004）。
/// 小时汇总和记录在同一个事务里写——WAL 下跨文件事务只按单个文件原子，
/// 所以它们必须同库；崩溃最多丢最后一个批次，而且两者一起丢，不会对不上。
/// </summary>
public sealed partial class TrafficWriter(
    TrafficRecorder recorder,
    IServiceScopeFactory scopeFactory,
    ILogger<TrafficWriter> logger) : BackgroundService
{
    /// <summary>一批最多这么多条。</summary>
    public const int MaxBatchSize = 100;

    /// <summary>攒批的时间窗。</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 已经从队列取出、还没落盘的记录。停机时它们不在队列里了，必须连同队列剩余一起刷掉，
        // 否则恰好在攒批窗口里停机就会丢掉这一批——这些行带着费用（Q32）。
        var batch = new List<RequestRecord>(MaxBatchSize);

        try
        {
            while (await recorder.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                using var window = new CancellationTokenSource(FlushInterval);

                while (batch.Count < MaxBatchSize)
                {
                    if (recorder.Reader.TryRead(out RequestRecord? record))
                    {
                        batch.Add(record);
                        continue;
                    }

                    using CancellationTokenSource linked =
                        CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, window.Token);
                    try
                    {
                        if (!await recorder.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (window.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                // 写入本身不跟停机信号走：半截取消的事务只会让这批白白回滚。
                if (batch.Count > 0)
                {
                    await PersistAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机：下面把手里这批和队列剩下的一起刷完。
        }

        await DrainAsync(batch).ConfigureAwait(false);
    }

    /// <summary>停机时把手里未落盘的和队列里剩下的全部写掉。</summary>
    private async Task DrainAsync(List<RequestRecord> pending)
    {
        var remaining = new List<RequestRecord>(pending);
        while (recorder.Reader.TryRead(out RequestRecord? record))
        {
            remaining.Add(record);
        }

        if (remaining.Count == 0)
        {
            return;
        }

        try
        {
            await PersistAsync(remaining, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or ObjectDisposedException or InvalidOperationException)
        {
            // 停机太晚、服务已经释放时也只记一行日志，不让主机以「后台服务失败」退出。
            LogDrainFailed(logger, ex, remaining.Count);
        }
    }

    private async Task PersistAsync(List<RequestRecord> batch, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        db.Requests.AddRange(batch);
        await ApplyAggregatesAsync(db, batch, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把这一批的用量累加到小时汇总。每条 Attempt 的用量归到它自己的渠道与上游模型，
    /// 请求计数归到成功的那一次（全失败就归到最后一次）。
    /// </summary>
    private static async Task ApplyAggregatesAsync(
        TrafficDbContext db,
        List<RequestRecord> batch,
        CancellationToken cancellationToken)
    {
        var deltas = new Dictionary<AggregateKey, HourlyAggregate>();

        foreach (RequestRecord request in batch)
        {
            foreach (AttemptRecord attempt in request.Attempts)
            {
                HourlyAggregate delta = Delta(deltas, request, attempt);

                delta.AttemptCount++;
                delta.InputTokens += attempt.InputTokens;
                delta.CacheReadTokens += attempt.CacheReadTokens;
                delta.CacheWrite5mTokens += attempt.CacheWrite5mTokens;
                delta.CacheWrite1hTokens += attempt.CacheWrite1hTokens;
                delta.OutputTokens += attempt.OutputTokens;
                delta.CostNanoUsd += attempt.CostNanoUsd ?? 0;

                // 渠道自己的问题才算尝试失败：客户端请求有误、客户端断开、手动中断不算（Q57）。
                if (attempt.FailureClass is not (FailureClass.None
                    or FailureClass.ClientRequestError
                    or FailureClass.ClientDisconnected
                    or FailureClass.Interrupted))
                {
                    delta.FailedAttemptCount++;
                }
            }

            // 请求级的计数记在「决定性」那次尝试的格子里：最后一次成功的，否则最后一次。
            // 一次 Attempt 都没有的请求（没选到渠道）也要进汇总，记在 ChannelId 0 上（Q73）。
            AttemptRecord? decisive = request.Attempts
                .OrderBy(a => a.Ordinal)
                .LastOrDefault(a => a.FailureClass == FailureClass.None)
                ?? request.Attempts.OrderBy(a => a.Ordinal).LastOrDefault();

            HourlyAggregate requestDelta = Delta(deltas, request, decisive);
            requestDelta.RequestCount++;

            switch (request.Status)
            {
                case RequestStatus.Succeeded:
                    requestDelta.SuccessCount++;
                    break;
                case RequestStatus.Failed:
                    requestDelta.FailureCount++;
                    break;
                case RequestStatus.Canceled or RequestStatus.Aborted:
                    requestDelta.CanceledCount++;
                    break;
                default:
                    break;
            }

            if (request.UsageSource == UsageSource.GatewayEstimated)
            {
                requestDelta.EstimatedCount++;
            }

            // 用了 Token 却算不出费用：单独记一笔，别让它看起来是免费的（Q44/Q65）。
            if (request.CostNanoUsd is null && (TotalTokens(request) > 0 || request.ImageCount > 0))
            {
                requestDelta.UnpricedCount++;
            }

            // 首字时间只有流式请求有意义（Q58）。
            if (request.IsStream && request.CommittedAt is { } committedAt)
            {
                requestDelta.AddTimeToFirstContent((long)(committedAt - request.StartedAt).TotalMilliseconds);
            }
        }

        await UpsertAsync(db, deltas, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 事后补上一个 Attempt 的用量和费用（background 响应的回填，Q277）：只加 Token 与费用，
    /// 不再计请求数和尝试数。<paramref name="newlyUnpriced"/> 为 true 时这个请求变成了「缺单价」。
    /// 调用方负责 SaveChanges。
    /// </summary>
    internal static Task AddBackfilledUsageAsync(
        TrafficDbContext db,
        RequestRecord request,
        AttemptRecord attempt,
        bool newlyUnpriced,
        CancellationToken cancellationToken)
    {
        var deltas = new Dictionary<AggregateKey, HourlyAggregate>();
        HourlyAggregate delta = Delta(deltas, request, attempt);
        delta.InputTokens += attempt.InputTokens;
        delta.CacheReadTokens += attempt.CacheReadTokens;
        delta.CacheWrite5mTokens += attempt.CacheWrite5mTokens;
        delta.CacheWrite1hTokens += attempt.CacheWrite1hTokens;
        delta.OutputTokens += attempt.OutputTokens;
        delta.CostNanoUsd += attempt.CostNanoUsd ?? 0;
        if (newlyUnpriced)
        {
            delta.UnpricedCount++;
        }

        return UpsertAsync(db, deltas, cancellationToken);
    }

    private static async Task UpsertAsync(
        TrafficDbContext db,
        Dictionary<AggregateKey, HourlyAggregate> deltas,
        CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        // EF 没有原生 upsert。这一批涉及的行很少（同一小时、少数渠道模型组合），一次查出来就够。
        List<DateTimeOffset> hours = [.. deltas.Keys.Select(k => k.Hour).Distinct()];
        List<HourlyAggregate> existing = await db.HourlyAggregates
            .Where(a => hours.Contains(a.HourUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var existingByKey = existing.ToDictionary(
            a => new AggregateKey(a.HourUtc, a.ChannelId, a.PublishedModel, a.UpstreamModel, a.ApiKeyId));

        foreach ((AggregateKey key, HourlyAggregate delta) in deltas)
        {
            if (existingByKey.TryGetValue(key, out HourlyAggregate? row))
            {
                row.Add(delta);

                // 渠道或 API 密钥改过名：老行跟着用新名字，删掉之后留住最后记下的那个（Q60）。
                row.ChannelName = delta.ChannelName;
                row.ApiKeyName = delta.ApiKeyName ?? row.ApiKeyName;
            }
            else
            {
                db.HourlyAggregates.Add(delta);
            }
        }
    }

    /// <summary>汇总行的 upsert 键。</summary>
    private readonly record struct AggregateKey(
        DateTimeOffset Hour,
        int ChannelId,
        string PublishedModel,
        string UpstreamModel,
        int ApiKeyId);

    /// <summary>
    /// 取（或建）这条记录该落的那个格子。<paramref name="attempt"/> 为 null 表示没选到任何渠道：
    /// ChannelId 0、上游模型留空（Q73）。
    /// </summary>
    private static HourlyAggregate Delta(
        Dictionary<AggregateKey, HourlyAggregate> deltas,
        RequestRecord request,
        AttemptRecord? attempt)
    {
        DateTimeOffset hour = Floor(attempt?.StartedAt ?? request.StartedAt);
        string published = Truncate(request.PublishedModel, 300);
        var key = new AggregateKey(
            hour,
            attempt?.ChannelId ?? 0,
            published,
            attempt?.UpstreamModel ?? string.Empty,
            request.ApiKeyId ?? 0);

        if (!deltas.TryGetValue(key, out HourlyAggregate? delta))
        {
            delta = new HourlyAggregate
            {
                HourUtc = hour,
                ChannelId = key.ChannelId,
                ChannelName = attempt?.ChannelName ?? string.Empty,
                PublishedModel = published,
                UpstreamModel = key.UpstreamModel,
                ApiKeyId = key.ApiKeyId,
                ApiKeyName = request.ApiKeyName,
            };
            deltas[key] = delta;
        }

        return delta;
    }

    private static long TotalTokens(RequestRecord request) =>
        request.InputTokens
        + request.CacheReadTokens
        + request.CacheWrite5mTokens
        + request.CacheWrite1hTokens
        + request.OutputTokens;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "停机时写入 {Count} 条请求记录失败。")]
    private static partial void LogDrainFailed(ILogger logger, Exception exception, int count);

    /// <summary>取所在小时的起点。先换到 UTC，否则东八区的时间会落到错的小时。</summary>
    private static DateTimeOffset Floor(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
