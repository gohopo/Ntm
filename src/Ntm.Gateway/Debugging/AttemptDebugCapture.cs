using System.Buffers;
using System.Net.Http.Headers;
using System.Text;

namespace Ntm.Gateway.Debugging;

/// <summary>
/// 一次 Attempt 的三份原文（Q156）。由 ChatRelay 建好传给转发层，转发层只往里填，
/// 不关心它最后去哪儿；Attempt 结束时由 ChatRelay 交给 <see cref="DebugCaptureSink"/>。
/// </summary>
public sealed class AttemptDebugCapture(Guid requestId, int attemptOrdinal, DateTimeOffset startedAt)
{
    private readonly List<DebugPayload> _payloads = [];
    // 攒原始字节，结束时一次解码：流式分块可能把一个多字节字符切成两半，逐块解码会出乱码。
    private readonly ArrayBufferWriter<byte> _responseBody = new();
    private int _responseBytes;
    private bool _responseTruncated;
    private string _responseHeaders = string.Empty;
    private int? _responseStatus;
    private bool _responseStarted;

    /// <summary>值里出现这些密钥就脱敏（Q158）。</summary>
    public IReadOnlyCollection<string> Secrets { get; init; } = [];

    /// <summary>客户端发来的原文。每个被录的 Attempt 都存一份，这样单看一次 Attempt 的三份原文就是完整的。</summary>
    public void CaptureClientRequest(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, ReadOnlySpan<byte> body)
    {
        Add(DebugPayloadKind.ClientRequest, DebugRedactor.Format(headers, Secrets), body, null);
    }

    /// <summary>网关真正发出去的请求。</summary>
    public void CaptureUpstreamRequest(HttpRequestMessage message, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(message);

        var headers = new List<KeyValuePair<string, IEnumerable<string>>>();
        foreach (KeyValuePair<string, IEnumerable<string>> header in message.Headers)
        {
            headers.Add(header);
        }

        if (message.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in message.Content.Headers)
            {
                headers.Add(header);
            }
        }

        Add(DebugPayloadKind.UpstreamRequest, DebugRedactor.Format(headers, Secrets), body, null);
    }

    /// <summary>上游响应的状态行和响应头。流式的正文随后一块块补进来。</summary>
    public void CaptureResponseHead(int statusCode, HttpResponseHeaders headers, HttpContentHeaders? contentHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var all = new List<KeyValuePair<string, IEnumerable<string>>>();
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            all.Add(header);
        }

        if (contentHeaders is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in contentHeaders)
            {
                all.Add(header);
            }
        }

        _responseStatus = statusCode;
        _responseHeaders = DebugRedactor.Format(all, Secrets);
        _responseStarted = true;
    }

    /// <summary>上游响应的字节。流式会反复调用；超过上限就停止记录，转发不受影响（Q157）。</summary>
    public void CaptureResponseChunk(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        _responseBytes += chunk.Length;
        int room = DebugPayload.MaxBytes - _responseBody.WrittenCount;
        if (room <= 0)
        {
            _responseTruncated = true;
            return;
        }

        if (chunk.Length > room)
        {
            _responseTruncated = true;
            chunk = chunk[..room];
        }

        _responseBody.Write(chunk);
    }

    /// <summary>这次 Attempt 录到的所有原文。</summary>
    public IReadOnlyList<DebugPayload> Finish()
    {
        if (_responseStarted)
        {
            _payloads.Add(new DebugPayload
            {
                RequestId = requestId,
                AttemptOrdinal = attemptOrdinal,
                Kind = DebugPayloadKind.UpstreamResponse,
                CapturedAt = startedAt,
                HttpStatusCode = _responseStatus,
                Headers = _responseHeaders,
                Body = Encoding.UTF8.GetString(_responseBody.WrittenSpan),
                Truncated = _responseTruncated,
                ByteCount = _responseBytes,
            });
            _responseStarted = false;
        }

        return _payloads;
    }

    private void Add(DebugPayloadKind kind, string headers, ReadOnlySpan<byte> body, int? status)
    {
        bool truncated = body.Length > DebugPayload.MaxBytes;
        ReadOnlySpan<byte> kept = truncated ? body[..DebugPayload.MaxBytes] : body;

        _payloads.Add(new DebugPayload
        {
            RequestId = requestId,
            AttemptOrdinal = attemptOrdinal,
            Kind = kind,
            CapturedAt = startedAt,
            HttpStatusCode = status,
            Headers = headers,
            Body = Encoding.UTF8.GetString(kept),
            Truncated = truncated,
            ByteCount = body.Length,
        });
    }
}
