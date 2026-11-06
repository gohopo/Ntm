using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 按上游密钥决定回什么的假上游。中继测试里每把 ChannelKey 对应一个剧本，
/// 这样能精确断言「哪把密钥被试了、按什么顺序」。
/// </summary>
internal sealed class ScriptedUpstream : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>>> _scripts =
        new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Secret, string Url, string Body)> Calls { get; } = new();

    /// <summary>每次调用收到的请求头（名字不分大小写，多值用逗号连起来），和 <see cref="Calls"/> 一一对应。</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> Headers { get; } = new();

    /// <summary>每次调用的 HTTP 方法，和 <see cref="Calls"/> 一一对应。</summary>
    public ConcurrentQueue<HttpMethod> Methods { get; } = new();

    public void Script(string secret, Func<HttpResponseMessage> response) =>
        _scripts[secret] = (_, _, _) => Task.FromResult(response());

    /// <summary>按方法、地址和请求体决定回什么。</summary>
    public void Script(string secret, Func<HttpMethod, Uri, string, HttpResponseMessage> response) =>
        _scripts[secret] = (request, body, _) => Task.FromResult(response(request.Method, request.RequestUri!, body));

    /// <summary>上游迟迟不回响应头，直到请求被取消。</summary>
    public void ScriptSilent(string secret) =>
        _scripts[secret] = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };

    /// <summary>上游先发这些帧，然后挂住不结束，直到请求被取消。</summary>
    public void ScriptHanging(string secret, params string[] frames) =>
        _scripts[secret] = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingStream(Encoding.UTF8.GetBytes(string.Concat(frames))))
            {
                Headers = { { "Content-Type", "text/event-stream" } },
            },
        });

    public IReadOnlyList<string> SecretsTried => [.. Calls.Select(c => c.Secret)];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // OpenAI 格式用 Authorization: Bearer，Anthropic 格式默认用 x-api-key（Q166）。
        string authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
            ? values.First()
            : request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? apiKeys)
                ? apiKeys.First()
                : string.Empty;
        string secret = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? authorization["Bearer ".Length..]
            : authorization;
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Headers.Enqueue(request.Headers.ToDictionary(
            h => h.Key,
            h => string.Join(",", h.Value),
            StringComparer.OrdinalIgnoreCase));
        Methods.Enqueue(request.Method);
        Calls.Enqueue((secret, request.RequestUri!.ToString(), body));

        return _scripts.TryGetValue(secret, out Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>>? script)
            ? await script(request, body, cancellationToken)
            : Json(HttpStatusCode.NotImplemented, "{\"error\":{\"message\":\"no script\"}}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Sse(params string[] frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(frames), Encoding.UTF8, "text/event-stream"),
    };

    /// <summary>读完给定的字节后一直挂住，读取被取消时抛出。</summary>
    private sealed class HangingStream(byte[] head) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < head.Length)
            {
                int count = Math.Min(buffer.Length, head.Length - _position);
                head.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
