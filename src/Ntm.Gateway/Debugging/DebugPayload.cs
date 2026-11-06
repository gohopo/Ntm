namespace Ntm.Gateway.Debugging;

/// <summary>一次 Attempt 会留下三份原文（Q156）。</summary>
public enum DebugPayloadKind
{
    /// <summary>客户端发来的原始请求（请求头 + 请求体）。</summary>
    ClientRequest = 1,

    /// <summary>网关真正发往上游的请求（改写、模型替换之后）。</summary>
    UpstreamRequest = 2,

    /// <summary>上游的响应（状态行 + 响应头 + 响应体，流式是原始 SSE 字节）。</summary>
    UpstreamResponse = 3,
}

/// <summary>
/// 一份调试原文。存在单独的 <c>ntm-debug.db</c> 里（Q153）：它比账大得多、留得短，
/// 放在一起会把流量库撑大，也会让 VACUUM 变成要停机的操作。
/// </summary>
public class DebugPayload
{
    /// <summary>单份原文的上限（Q157）。超了就截断，并记下 <see cref="Truncated"/>。</summary>
    public const int MaxBytes = 1024 * 1024;

    public long Id { get; set; }

    /// <summary>所属 Request。调试库和流量库之间没有外键，两边各自按时间清理。</summary>
    public Guid RequestId { get; set; }

    /// <summary>所属 Attempt 的序号，和 AttemptRecord.Ordinal 对应。</summary>
    public int AttemptOrdinal { get; set; }

    public DebugPayloadKind Kind { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>HTTP 状态码，只有上游响应有。</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>请求头或响应头，一行一个 <c>name: value</c>，敏感值已脱敏（Q158）。</summary>
    public string Headers { get; set; } = string.Empty;

    /// <summary>原文正文。按 UTF-8 存文本：这些接口都是 JSON 或 SSE。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>正文是否因为超过 <see cref="MaxBytes"/> 被截断。</summary>
    public bool Truncated { get; set; }

    /// <summary>正文的原始字节数，截断前的真实大小。</summary>
    public int ByteCount { get; set; }
}
