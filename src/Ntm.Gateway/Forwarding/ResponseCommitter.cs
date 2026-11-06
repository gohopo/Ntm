using System.Buffers;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 管「提交点」这一件事（ADR-0008）：第一个 ContentEvent 写给客户端之前，一切都还能撤回。
///
/// 序幕事件先缓冲，等第一个 ContentEvent 到了再按原顺序一起放出去。
/// 等不到就在上限时刻先把响应头发出去并开始发 <c>: keepalive</c> 注释行，重试窗口仍然开着——
/// 发出响应头不是提交。
/// </summary>
public sealed class ResponseCommitter : IDisposable
{
    /// <summary>等第一个 ContentEvent 的上限，到点先发响应头保住连接（Q25）。</summary>
    public static readonly TimeSpan DefaultHeaderDelay = TimeSpan.FromSeconds(10);

    /// <summary>发了响应头还没提交时，每隔这么久发一个注释行。</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);

    private static readonly byte[] KeepAliveFrame = ": keepalive\n\n"u8.ToArray();

    private readonly HttpResponse _response;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _headerDelay;
    private readonly ArrayBufferWriter<byte> _preamble = new();

    private bool _headersSent;
    private bool _disposed;

    public ResponseCommitter(HttpResponse response, TimeProvider? timeProvider = null, TimeSpan? headerDelay = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        _response = response;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _headerDelay = headerDelay ?? DefaultHeaderDelay;
    }

    /// <summary>第一个 ContentEvent 已经写给客户端了。此后不能再换候选。</summary>
    public bool Committed { get; private set; }

    /// <summary>提交那一刻调用一次：实时链路的 Commit 事件从这里发出（Q40/Q113）。</summary>
    public Action? OnCommitted { get; set; }

    /// <summary>响应头已经发出。这不等于提交。</summary>
    public bool HeadersSent => _headersSent || _response.HasStarted;

    /// <summary>缓冲中的序幕字节数。</summary>
    public int BufferedBytes => _preamble.WrittenCount;

    /// <summary>响应头已发出但还没提交时，到了该发 keepalive 的时刻。</summary>
    public DateTimeOffset? NextKeepAliveAt { get; private set; }

    /// <summary>发出请求的时刻，用来算响应头上限。</summary>
    public DateTimeOffset? RequestStartedAt { get; private set; }

    /// <summary>标记请求已发出，开始计响应头上限。</summary>
    public void MarkRequestStarted() => RequestStartedAt ??= _timeProvider.GetUtcNow();

    /// <summary>到了该先发响应头的时刻。</summary>
    public bool ShouldSendHeadersNow() =>
        !HeadersSent
        && RequestStartedAt is { } startedAt
        && _timeProvider.GetUtcNow() - startedAt >= _headerDelay;

    /// <summary>序幕事件：先存着，不写给客户端。</summary>
    public void BufferPreamble(ReadOnlySpan<byte> frameBytes) => _preamble.Write(frameBytes);

    /// <summary>
    /// 把上游的状态码与响应头搬给客户端。逐跳头不转发——帧由 Kestrel 自己管。
    /// </summary>
    public void CopyUpstreamHead(HttpResponseMessage upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HeadersSent)
        {
            // 已经按上限发过 200 了，改不了状态码，失败只能走流内错误事件。
            return;
        }

        _response.StatusCode = (int)upstream.StatusCode;
        CopyHeaders(upstream.Headers);
        CopyHeaders(upstream.Content.Headers);
    }

    /// <summary>
    /// 在上限时刻先把响应头发出去，之后开始发 keepalive。重试窗口仍然开着。
    /// </summary>
    public async Task SendStreamHeadersEarlyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HeadersSent)
        {
            return;
        }

        _response.StatusCode = StatusCodes.Status200OK;
        _response.ContentType = "text/event-stream";
        _response.Headers.CacheControl = "no-cache";

        // 前置 nginx 默认会缓冲 SSE。
        _response.Headers["X-Accel-Buffering"] = "no";

        DisableBuffering();
        await _response.StartAsync(cancellationToken).ConfigureAwait(false);
        _headersSent = true;
        ScheduleKeepAlive();
    }

    /// <summary>
    /// 第一个 ContentEvent：补发响应头，放出缓冲的序幕，再写这一帧。写完就算提交。
    /// </summary>
    public async Task CommitAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Committed)
        {
            throw new InvalidOperationException("已经提交过了。");
        }

        StopKeepAlive();

        if (!HeadersSent)
        {
            DisableBuffering();
            await _response.StartAsync(cancellationToken).ConfigureAwait(false);
            _headersSent = true;
        }

        if (_preamble.WrittenCount > 0)
        {
            await _response.Body.WriteAsync(_preamble.WrittenMemory, cancellationToken).ConfigureAwait(false);
            _preamble.Clear();
        }

        if (!frameBytes.IsEmpty)
        {
            await _response.Body.WriteAsync(frameBytes, cancellationToken).ConfigureAwait(false);
        }

        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        Committed = true;
        OnCommitted?.Invoke();
    }

    /// <summary>提交之后的字节：直接写过去。</summary>
    public async Task WriteCommittedAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Committed)
        {
            throw new InvalidOperationException("还没提交，字节应该先进序幕缓冲。");
        }

        await _response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发一个 SSE 注释行维持连接。客户端会忽略它，所以这不算提交。</summary>
    public async Task WriteKeepAliveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!HeadersSent || Committed)
        {
            return;
        }

        await _response.Body.WriteAsync(KeepAliveFrame, cancellationToken).ConfigureAwait(false);
        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        ScheduleKeepAlive();
    }

    /// <summary>
    /// 放弃重试，把缓冲的序幕原样交给客户端。
    /// 用在上游干净地结束了流但一个 ContentEvent 都没有的时候：字节是上游给的，照样交出去，
    /// 但这不是「提交了一个 ContentEvent」，所以和 <see cref="CommitAsync"/> 分开。
    /// </summary>
    public async Task FlushUncommittedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Committed)
        {
            return;
        }

        StopKeepAlive();

        if (!HeadersSent)
        {
            DisableBuffering();
            await _response.StartAsync(cancellationToken).ConfigureAwait(false);
            _headersSent = true;
        }

        if (_preamble.WrittenCount > 0)
        {
            await _response.Body.WriteAsync(_preamble.WrittenMemory, cancellationToken).ConfigureAwait(false);
            _preamble.Clear();
        }

        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>换候选：扔掉这一轮缓冲的序幕。响应头如果已经发了就收不回来。</summary>
    public void DiscardPreamble()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Committed)
        {
            throw new InvalidOperationException("提交之后不能再换候选。");
        }

        _preamble.Clear();
    }

    private void ScheduleKeepAlive() => NextKeepAliveAt = _timeProvider.GetUtcNow() + KeepAliveInterval;

    /// <summary>
    /// keepalive 的节奏由转发循环驱动：它在等上游字节时顺便看一眼 <see cref="NextKeepAliveAt"/>，
    /// 不另起定时器，省掉一份和响应体并发写的风险。
    /// </summary>
    private void StopKeepAlive() => NextKeepAliveAt = null;

    private void DisableBuffering() =>
        _response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    private void CopyHeaders(HttpHeaders headers)
    {
        foreach ((string name, IEnumerable<string> values) in headers)
        {
            if (IsHopByHop(name))
            {
                continue;
            }

            _response.Headers[name] = values.ToArray();
        }
    }

    /// <summary>
    /// 不搬给客户端的头。逐跳头由本地连接决定；上游账号相关的头会暴露网关背后是哪个组织、
    /// 哪把密钥还剩多少额度（上游凭证留在网关）；<c>Content-Length</c> 在剔除注入的用量帧后不再准确，
    /// 交给 Kestrel 自己决定分块。
    /// </summary>
    private static bool IsHopByHop(string name) =>
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)

        // Server、Date 由 Kestrel 自己写；照搬会出现两个 Server 头，还暴露上游用的是什么。
        || name.Equals("Server", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("openai-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase)
        || IsConnectionScoped(name);

    private static bool IsConnectionScoped(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopKeepAlive();
    }
}
