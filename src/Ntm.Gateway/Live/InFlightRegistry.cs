using System.Collections.Concurrent;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Live;

/// <summary>
/// 进行中的 Request（Q94/Q112）。实时链路、日志页的「进行中」、中断和进行中请求的详情都从这里取；
/// 每个状态转换顺手发一条实时事件。并发上限不靠它数——那是 ConcurrencyGauges，O(1)。
/// </summary>
public sealed class InFlightRegistry(LiveHub? hub = null)
{
    private readonly ConcurrentDictionary<Guid, InFlightRequest> _requests = new();

    public InFlightRequest Register(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entry = new InFlightRequest(record, hub);
        _requests[record.Id] = entry;
        hub?.Publish(new RequestStartedEvent(entry.ToLive()));
        return entry;
    }

    /// <summary>Request 结束：发结束事件，再从表里拿掉。记录这时已经交给写盘队列了。</summary>
    public void Complete(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_requests.TryRemove(record.Id, out InFlightRequest? entry))
        {
            return;
        }

        DateTimeOffset finishedAt = record.FinishedAt ?? record.StartedAt;
        hub?.Publish(new RequestFinishedEvent(
            record.Id,
            record.Status,
            finishedAt,
            (long)(finishedAt - record.StartedAt).TotalMilliseconds,
            record.HttpStatusCode,
            record.InputTokens,
            record.OutputTokens,
            record.UsageSource,
            record.CostNanoUsd));
        entry.Dispose();
    }

    public InFlightRequest? Find(Guid requestId) => _requests.GetValueOrDefault(requestId);

    /// <summary>请求中断（Q115）。不在进行中（已经结束或不存在）时返回 false。</summary>
    public bool TryInterrupt(Guid requestId) =>
        _requests.TryGetValue(requestId, out InFlightRequest? entry) && entry.RequestInterrupt(InterruptReason.Admin);

    /// <summary>停机时把还没结束的请求都中断掉，返回中断了几个。</summary>
    public int InterruptAllForShutdown() =>
        _requests.Values.Count(entry => entry.RequestInterrupt(InterruptReason.Shutdown));

    public int Count => _requests.Count;

    public IReadOnlyList<InFlightRequest> Snapshot() => [.. _requests.Values];

    /// <summary>当前 Attempt 落在这个渠道上的个数。</summary>
    public int CountOnChannel(int channelId) => _requests.Values.Count(r => r.CurrentChannelId == channelId);
}

/// <summary>
/// 一个进行中的 Request。只有 ID、名字和时刻，不含请求体（Q40）。
/// Attempt 由转发线程写，实时端点和详情页从别的线程读，所以都在锁里换成不可变的 <see cref="LiveAttempt"/>。
/// </summary>
public sealed class InFlightRequest : IDisposable
{
    private readonly LiveHub? _hub;
    private readonly Lock _gate = new();
    private readonly List<LiveAttempt> _attempts = [];
    private readonly CancellationTokenSource _interrupt = new();
    private DateTimeOffset? _committedAt;
    private bool _disposed;
    private InterruptReason _interruptReason;

    internal InFlightRequest(RequestRecord record, LiveHub? hub)
    {
        _hub = hub;
        RequestId = record.Id;
        StartedAt = record.StartedAt;
        ApiKeyId = record.ApiKeyId;
        ApiKeyName = record.ApiKeyName;
        Origin = record.Origin;
        PublishedModel = record.PublishedModel;
        RequestKind = record.RequestKind;
        IsStream = record.IsStream;
        InboundApiFormat = record.InboundApiFormat;
    }

    public Guid RequestId { get; }

    public DateTimeOffset StartedAt { get; }

    public int? ApiKeyId { get; }

    public string? ApiKeyName { get; }

    public RequestOrigin Origin { get; }

    public string PublishedModel { get; }

    public RequestKind RequestKind { get; }

    public bool IsStream { get; }

    public ApiFormat InboundApiFormat { get; }

    /// <summary>Admin 请求中断时取消（Q115）。转发把它和客户端断开的令牌连在一起用。</summary>
    public CancellationToken InterruptToken => _interrupt.Token;

    public bool InterruptRequested => _interrupt.IsCancellationRequested;

    /// <summary>谁要求的中断；没有中断时是 <see cref="InterruptReason.None"/>。</summary>
    public InterruptReason InterruptReason
    {
        get
        {
            lock (_gate)
            {
                return _interruptReason;
            }
        }
    }

    public int? CurrentChannelId
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count == 0 ? null : _attempts[^1].ChannelId;
            }
        }
    }

    public IReadOnlyList<LiveAttempt> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    public DateTimeOffset? CommittedAt
    {
        get
        {
            lock (_gate)
            {
                return _committedAt;
            }
        }
    }

    public void AttemptStarted(AttemptRecord attempt)
    {
        LiveAttempt live = LiveAttempt.From(attempt);
        lock (_gate)
        {
            _attempts.Add(live);
        }

        _hub?.Publish(new AttemptStartedEvent(RequestId, live));
    }

    public void AttemptFinished(AttemptRecord attempt)
    {
        LiveAttempt live = LiveAttempt.From(attempt);
        lock (_gate)
        {
            int index = _attempts.FindLastIndex(a => a.AttemptNumber == live.AttemptNumber);
            if (index >= 0)
            {
                _attempts[index] = live;
            }
            else
            {
                _attempts.Add(live);
            }
        }

        _hub?.Publish(new AttemptFinishedEvent(RequestId, live));
    }

    public void Committed(int attemptNumber, DateTimeOffset committedAt)
    {
        lock (_gate)
        {
            _committedAt = committedAt;
        }

        long? ttfc = IsStream ? (long)(committedAt - StartedAt).TotalMilliseconds : null;
        _hub?.Publish(new CommittedEvent(RequestId, attemptNumber, committedAt, ttfc));
    }

    public LiveRequest ToLive()
    {
        lock (_gate)
        {
            return new LiveRequest(
                RequestId,
                StartedAt,
                ApiKeyId,
                ApiKeyName,
                Origin,
                PublishedModel,
                RequestKind,
                IsStream,
                _committedAt,
                _interrupt.IsCancellationRequested,
                [.. _attempts]);
        }
    }

    internal bool RequestInterrupt(InterruptReason reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            // 已经中断过的不改原因：Admin 先点了中断，停机时仍算 Admin 中断。
            if (!_interrupt.IsCancellationRequested)
            {
                _interruptReason = reason;
                _interrupt.Cancel();
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _interrupt.Dispose();
    }
}

/// <summary>请求为什么被中断。</summary>
public enum InterruptReason
{
    None = 0,

    /// <summary>Admin 在请求日志里点了中断（Q115）。</summary>
    Admin = 1,

    /// <summary>网关停机，等够了还没结束的请求被切掉（Q343）。</summary>
    Shutdown = 2,
}
