using System.Threading.Channels;

namespace Ntm.Gateway.Live;

/// <summary>
/// 实时链路的分发（Q40/Q114）：一个全局流，每个订阅者一个有界缓冲。
/// 发布从不阻塞转发路径；谁的缓冲满了就把谁断开，它重连时重新拿全量，而不是让缓冲无限长。
/// </summary>
public sealed class LiveHub
{
    /// <summary>每个订阅者最多积压这么多条，满了就断开它。</summary>
    public const int SubscriberBuffer = 256;

    /// <summary>同时订阅的上限；超过的拒绝（端点回 429）。</summary>
    public const int MaxSubscribers = 20;

    private readonly Lock _gate = new();
    private readonly HashSet<LiveSubscription> _subscribers = [];

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>订阅；到上限时返回 null。先订阅再取全量，中间发生的事件不会漏（重复的前端按 ID 去重）。</summary>
    public LiveSubscription? TrySubscribe()
    {
        lock (_gate)
        {
            if (_subscribers.Count >= MaxSubscribers)
            {
                return null;
            }

            var subscription = new LiveSubscription(this);
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    public void Publish(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        LiveSubscription[] targets;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            targets = [.. _subscribers];
        }

        foreach (LiveSubscription subscription in targets)
        {
            if (!subscription.TryWrite(liveEvent))
            {
                subscription.Overflow();
            }
        }
    }

    internal void Remove(LiveSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }
}

/// <summary>一个订阅者。读完 <see cref="Reader"/> 就是被断开了（缓冲满或被释放）。</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly LiveHub _hub;
    private readonly Channel<LiveEvent> _channel = Channel.CreateBounded<LiveEvent>(
        new BoundedChannelOptions(LiveHub.SubscriberBuffer)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    internal LiveSubscription(LiveHub hub) => _hub = hub;

    public ChannelReader<LiveEvent> Reader => _channel.Reader;

    /// <summary>缓冲满了被断开的；端点据此记一条日志。</summary>
    public bool Overflowed { get; private set; }

    internal bool TryWrite(LiveEvent liveEvent) => _channel.Writer.TryWrite(liveEvent);

    internal void Overflow()
    {
        Overflowed = true;
        _channel.Writer.TryComplete();
        _hub.Remove(this);
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _hub.Remove(this);
    }
}
