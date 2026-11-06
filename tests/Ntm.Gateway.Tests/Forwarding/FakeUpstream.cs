using System.Net;
using System.Threading.Channels;

namespace Ntm.Gateway.Tests.Forwarding;

/// <summary>
/// 一个可编排的假上游。测试可以一段一段地放字节，所以能验到「序幕到了但内容还没到」这种中间状态。
/// </summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _requestReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeUpstream(HttpStatusCode status = HttpStatusCode.OK, string contentType = "text/event-stream")
    {
        Status = status;
        ContentType = contentType;
    }

    public HttpStatusCode Status { get; set; }

    public string ContentType { get; set; }

    /// <summary>上游要回的响应头。</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>收到的请求，供断言用。</summary>
    public HttpRequestMessage? ReceivedRequest { get; private set; }

    public string? ReceivedBody { get; private set; }

    /// <summary>请求已经打到上游。</summary>
    public Task RequestReceived => _requestReceived.Task;

    /// <summary>发送异常而不是响应。</summary>
    public Exception? ThrowOnSend { get; set; }

    /// <summary>放一段字节给客户端。</summary>
    public void Write(string text) => _chunks.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>流结束。</summary>
    public void Complete() => _chunks.Writer.TryComplete();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ReceivedRequest = request;
        if (request.Content is not null)
        {
            ReceivedBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        _requestReceived.TrySetResult();

        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        var response = new HttpResponseMessage(Status)
        {
            Content = new StreamContent(new ChannelStream(_chunks.Reader)),
            RequestMessage = request,
        };

        response.Content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
        foreach ((string name, string value) in ResponseHeaders)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    /// <summary>把一个 channel 当成流读。</summary>
    private sealed class ChannelStream(ChannelReader<byte[]> reader) : Stream
    {
        private ReadOnlyMemory<byte> _remainder;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remainder.IsEmpty)
            {
                try
                {
                    _remainder = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            int take = Math.Min(buffer.Length, _remainder.Length);
            _remainder[..take].CopyTo(buffer);
            _remainder = _remainder[take..];
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
