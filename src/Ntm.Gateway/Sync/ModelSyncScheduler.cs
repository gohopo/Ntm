using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Sync;

/// <summary>ModelSync 的后台配置（Q141）。</summary>
public sealed class ModelSyncOptions
{
    public const string SectionName = "Ntm:ModelSync";

    /// <summary>
    /// 每隔多少分钟把所有开了同步的渠道过一遍，默认 60。
    /// 设 0 关掉后台同步，新建渠道也不自动同步，只剩渠道页的「立即同步」。
    /// </summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>启动后等多久开始第一轮，默认 60 秒：别和启动时的迁移、价目刷新挤在一起。</summary>
    public int StartDelaySeconds { get; set; } = 60;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public TimeSpan StartDelay => TimeSpan.FromSeconds(StartDelaySeconds);
}

/// <summary>
/// 每小时把开了同步的渠道依次过一遍（Q141）：按渠道 ID 错开在这一小时里，一个接一个跑，
/// 不在整点一起打所有上游。停用的渠道跳过。新建的渠道通过 <see cref="Enqueue"/> 马上同步一次。
/// </summary>
public sealed partial class ModelSyncScheduler(
    ModelSyncService sync,
    IServiceScopeFactory scopeFactory,
    IOptions<ModelSyncOptions> options,
    TimeProvider timeProvider,
    ILogger<ModelSyncScheduler> logger) : BackgroundService
{
    private readonly Channel<int> _immediate = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>排一次马上同步，例如刚建好的渠道。后台同步关掉时什么也不做。</summary>
    public void Enqueue(int channelId)
    {
        if (options.Value.Interval > TimeSpan.Zero)
        {
            _immediate.Writer.TryWrite(channelId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Interval <= TimeSpan.Zero)
        {
            return;
        }

        await Task.WhenAll(RunImmediateAsync(stoppingToken), RunCyclesAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RunImmediateAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (int channelId in _immediate.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeSyncAsync(channelId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunCyclesAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = options.Value.Interval;
        try
        {
            await Task.Delay(options.Value.StartDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset cycleStart = timeProvider.GetUtcNow();
                foreach (int channelId in await ListAsync(stoppingToken).ConfigureAwait(false))
                {
                    // 按 ID 在周期里占一个固定的位置：渠道多少变了，其余渠道的时刻也不跟着挪。
                    TimeSpan offset = TimeSpan.FromTicks(interval.Ticks / 60 * (channelId % 60));
                    TimeSpan wait = cycleStart + offset - timeProvider.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, timeProvider, stoppingToken).ConfigureAwait(false);
                    }

                    if (await StillEnabledAsync(channelId, stoppingToken).ConfigureAwait(false))
                    {
                        await SafeSyncAsync(channelId, stoppingToken).ConfigureAwait(false);
                    }
                }

                TimeSpan rest = cycleStart + interval - timeProvider.GetUtcNow();
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, timeProvider, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>开了同步的启用渠道，按它们在周期里的位置排好。</summary>
    private async Task<List<int>> ListAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        List<int> ids = await db.Channels
            .Where(c => c.Enabled && c.ModelSyncEnabled)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. ids.OrderBy(id => id % 60).ThenBy(id => id)];
    }

    private async Task<bool> StillEnabledAsync(int channelId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        return await db.Channels
            .AnyAsync(c => c.Id == channelId && c.Enabled && c.ModelSyncEnabled, cancellationToken).ConfigureAwait(false);
    }

    private async Task SafeSyncAsync(int channelId, CancellationToken cancellationToken)
    {
        try
        {
            await sync.SyncAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // 一个渠道的意外错误不能让后台同步整个停掉。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpected(logger, ex, channelId);
        }
    }

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "渠道 #{ChannelId} 的模型同步出了意外错误")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, int channelId);
}
