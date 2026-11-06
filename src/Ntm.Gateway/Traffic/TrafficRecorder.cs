using System.Threading.Channels;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 请求记录的入口。队列无界、不丢（Q32）：这些行带着 Cost，它们是账。
/// 只有调试原文那条队列允许在压力下丢。
/// </summary>
public sealed class TrafficRecorder(GatewayMetrics? metrics = null)
{
    private readonly Channel<RequestRecord> _queue = Channel.CreateUnbounded<RequestRecord>(
        new UnboundedChannelOptions
        {
            // 实际只有 TrafficWriter 一个读者，但单读者的无界队列不支持 Count，/metrics 要读队列长度（Q339）。
            SingleReader = false,
            AllowSynchronousContinuations = false,
        });

    public ChannelReader<RequestRecord> Reader => _queue.Reader;

    /// <summary>还在排队、没写进库的记录数。</summary>
    public int Pending => _queue.Reader.Count;

    /// <summary>排一条完成的请求记录（连同它的 Attempt）。</summary>
    public void Enqueue(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        metrics?.Observe(record);

        if (!_queue.Writer.TryWrite(record))
        {
            throw new InvalidOperationException("请求记录队列已关闭。");
        }
    }

    /// <summary>关队列，让写入器把剩下的刷完。</summary>
    public void Complete() => _queue.Writer.TryComplete();
}
