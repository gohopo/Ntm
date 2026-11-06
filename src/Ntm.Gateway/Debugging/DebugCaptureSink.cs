using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Debugging;

/// <summary>
/// 调试原文的落盘队列（Q160）。队列满了就丢新的并记数：原文是用来排查问题的，
/// 为了它拖慢转发不值得。界面上会显示丢了多少份。
/// </summary>
public sealed partial class DebugCaptureSink : BackgroundService
{
    private readonly Channel<DebugPayload> _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly DebugCaptureOptions _options;
    private readonly ILogger<DebugCaptureSink> _logger;
    private long _dropped;
    private long _written;

    public DebugCaptureSink(
        IServiceScopeFactory scopes,
        IOptions<DebugCaptureOptions> options,
        ILogger<DebugCaptureSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<DebugPayload>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            // 不能用 DropWrite：那种模式下 TryWrite 丢掉了也返回 true，丢了多少就数不出来。
            // Wait 模式下 TryWrite 在满时返回 false，同样不阻塞。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>因为写盘跟不上而被丢掉的份数，进程内累计。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>已经落盘的份数，进程内累计。测试用。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>把一份原文排进队列。永不阻塞，永不抛。</summary>
    public void Enqueue(DebugPayload payload)
    {
        if (!_queue.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<DebugPayload>(_options.WriteBatchSize);

        while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < _options.WriteBatchSize && _queue.Reader.TryRead(out DebugPayload? payload))
            {
                batch.Add(payload);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                DebugDbContext db = scope.ServiceProvider.GetRequiredService<DebugDbContext>();
                db.DebugPayloads.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                Interlocked.Add(ref _written, batch.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
            {
                // 写不进去就算了：原文没了不影响转发，也不该让后台任务停掉。
                Interlocked.Add(ref _dropped, batch.Count);
                LogWriteFailed(_logger, batch.Count, ex);
            }
        }
    }

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "{Count} 份调试原文写入失败，已丢弃。")]
    private static partial void LogWriteFailed(ILogger logger, int count, Exception exception);
}
