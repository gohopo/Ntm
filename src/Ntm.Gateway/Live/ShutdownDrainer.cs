using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ntm.Gateway.Live;

/// <summary>
/// 停机时先等进行中的请求自己结束，等够了把剩下的中断掉（Q343）。
/// 不这样做的话，比停机等待时间还长的流会被 Kestrel 直接切断，
/// 写入器再落盘时服务已经释放，这些请求的记录就丢了。
/// </summary>
public sealed partial class ShutdownDrainer(InFlightRegistry inFlight, TimeProvider timeProvider, ILogger<ShutdownDrainer> logger)
    : IHostedLifecycleService
{
    /// <summary>进行中的请求最多再等这么久。</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>中断之后给它们写完错误、交出记录的时间。</summary>
    public static readonly TimeSpan AfterInterrupt = TimeSpan.FromSeconds(5);

    /// <summary>整个停机的等待上限：上面两段加上写入器落盘的时间。</summary>
    public static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    internal TimeSpan GraceOverride { get; init; } = Grace;

    internal TimeSpan AfterInterruptOverride { get; init; } = AfterInterrupt;

    public Task StoppingAsync(CancellationToken cancellationToken) => DrainAsync(cancellationToken);

    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        int waiting = inFlight.Count;
        if (waiting == 0)
        {
            return;
        }

        LogWaiting(logger, waiting, (int)GraceOverride.TotalSeconds);
        if (await WaitUntilEmptyAsync(GraceOverride, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        int interrupted = inFlight.InterruptAllForShutdown();
        LogInterrupted(logger, interrupted);
        await WaitUntilEmptyAsync(AfterInterruptOverride, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilEmptyAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + limit;
        while (inFlight.Count > 0)
        {
            if (timeProvider.GetUtcNow() >= deadline || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2401, Level = LogLevel.Information, Message = "网关正在关闭，等 {Count} 个进行中的请求结束（最多 {Seconds} 秒）")]
    private static partial void LogWaiting(ILogger logger, int count, int seconds);

    [LoggerMessage(EventId = 2402, Level = LogLevel.Warning, Message = "等够了还有 {Count} 个请求没结束，已中断")]
    private static partial void LogInterrupted(ILogger logger, int count);
}
