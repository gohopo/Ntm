using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Forwarding;

/// <summary>一次上游调用要用到的全部输入。</summary>
public sealed class ForwardRequest
{
    public required Uri RequestUri { get; init; }

    /// <summary>
    /// HTTP 方法。中继都是 POST；按 id 取回、删除 Responses 资源时是 GET / DELETE（ADR-0020），
    /// 这时 <see cref="Body"/> 为空就不带请求体。
    /// </summary>
    public HttpMethod Method { get; init; } = HttpMethod.Post;

    /// <summary>渠道的出站代理（Q130）。null 表示直连。</summary>
    public Upstream.ProxyAddress? Proxy { get; init; }

    /// <summary>密钥原文。只在内存里，不落日志（ADR-0006）。</summary>
    public required string Secret { get; init; }

    public required ApiFormat UpstreamApiFormat { get; init; }

    /// <summary>客户端用的格式。和上游相同时走字节透传（ADR-0002）。</summary>
    public required ApiFormat InboundApiFormat { get; init; }

    public RequestKind RequestKind { get; init; } = RequestKind.Chat;

    /// <summary>要发给上游的请求体。可重放：换候选要重发同样的字节。</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    public bool IsStream { get; init; }

    /// <summary>
    /// 请求体里的 <c>include_usage</c> 是我们注入的、客户端自己没要：
    /// 转发时把上游多回的那个只带 usage 的块剔掉（ADR-0002）。
    /// </summary>
    public bool SwallowUsageOnlyFrames { get; init; }

    /// <summary>客户端带来的请求头里需要转发的部分，认证头已经剔除。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ForwardedHeaders { get; init; } = [];

    /// <summary>渠道的改写规则（Q144）。请求体规则已经体现在 <see cref="Body"/> 里，这里只用它的请求头规则。</summary>
    public Rewrite.RequestRewriter Rewriter { get; init; } = Rewrite.RequestRewriter.Empty;

    /// <summary>这次 Attempt 的调试原文收集器；null 表示没开 DebugCapture（Q155）。</summary>
    public Debugging.AttemptDebugCapture? Debug { get; init; }

    /// <summary>Anthropic 渠道用 <c>Authorization: Bearer</c> 而不是 <c>x-api-key</c>（Q166）。</summary>
    public bool AnthropicBearerAuth { get; init; }

    public bool Passthrough => InboundApiFormat == UpstreamApiFormat;

    /// <summary>格式不同时把上游响应翻译回客户端格式所需的信息（ADR-0001）。透传时为空。</summary>
    public ResponseConversion? Conversion { get; init; }

    /// <summary>
    /// 入站与上游格式不同，响应要经由 IR 写回客户端格式。三种格式两两之间都走这一条路（ADR-0015）。
    /// </summary>
    public bool Converts => Conversion is not null && InboundApiFormat != UpstreamApiFormat;
}

/// <summary>翻译响应时要用的、在多次尝试之间保持不变的值。</summary>
public sealed class ResponseConversion
{
    /// <summary>完整消息 id（含 <c>msg_</c> 前缀）。按请求生成，换候选不变。</summary>
    public required string MessageId { get; init; }

    /// <summary>上游没报模型名时写给客户端的模型名。</summary>
    public required string FallbackModel { get; init; }

    /// <summary>
    /// 客户端自己要了用量没有。只影响 OpenAI Chat 方向的收尾：
    /// 没要就不写那个只带 usage 的分片（Q176）。
    /// </summary>
    public bool ClientWantsUsage { get; init; }
}

/// <summary>一次上游调用的结果。</summary>
public sealed class ForwardOutcome
{
    public bool Committed { get; init; }

    /// <summary>可写：转发器只知道令牌被取消，分不清是客户端断开还是 Admin 中断，由中继改写（Q115）。</summary>
    public FailureClass FailureClass { get; set; }

    public int? HttpStatusCode { get; init; }

    public Usage Usage { get; init; } = Usage.Unavailable;

    /// <summary>
    /// 只拿到输入侧的用量。Anthropic 流在 <c>message_start</c> 里就给了输入，
    /// 输出要等 <c>message_delta</c>；中途断掉时输入用真数，输出靠估算（Q177）。
    /// </summary>
    public Usage PartialUsage { get; init; } = Usage.Unavailable;

    /// <summary>已经看到的输出里的 ASCII 字符数（流式才有）。上游没给用量时用来估算输出（Q47）。</summary>
    public long OutputAsciiChars { get; init; }

    /// <summary>已经看到的输出里的非 ASCII 字符数（流式才有）。</summary>
    public long OutputWideChars { get; init; }

    /// <summary>上游输出了思考过程。决定输出按哪一档单价算（ADR-0013）。</summary>
    public bool SawThinking { get; init; }

    public string? UpstreamModel { get; init; }

    /// <summary>Responses 上游给的响应 id。成功落地后记进落点表，供 previous_response_id 找回（ADR-0016）。</summary>
    public string? ResponseId { get; init; }

    public string? FinishReason { get; init; }

    public DateTimeOffset? FirstEventAt { get; init; }

    public DateTimeOffset? FirstContentEventAt { get; init; }

    /// <summary>截断后的错误摘要。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 上游非 2xx 时的错误体原文（有界）。只在把客户端请求错误原样返回时用（Q28），
    /// 其余失败不透给客户端，免得把上游认证或额度细节漏出去。
    /// </summary>
    public string? ErrorBody { get; init; }

    public string? ErrorContentType { get; init; }

    /// <summary>上游干净地结束了流，但一个 ContentEvent 都没有。</summary>
    public bool EndedEmpty { get; init; }

    /// <summary>图片请求生成的张数，按张计价用（Q230）。别的请求是 0。</summary>
    public int ImageCount { get; init; }

    /// <summary>
    /// 提交之后失败，转发器已经按客户端格式写过流内错误事件，调用方不要再写一次。
    /// 透传时上游自己的错误帧已经原样转发，也算告知过。
    /// </summary>
    public bool ClientNotified { get; init; }

    /// <summary>
    /// 上游在响应头里给出的恢复时刻（Retry-After、x-ratelimit-reset-*、anthropic-ratelimit-*-reset）。
    /// 有值时冷却以它为准，不走指数退避（Q29）。只有限流、额度用尽、过载这类
    /// 「等一会儿就好」的失败才会带上。
    /// </summary>
    public DateTimeOffset? UpstreamResetAt { get; init; }

    public bool Succeeded => FailureClass == FailureClass.None;
}

/// <summary>转发用到的超时（Q30）。</summary>
public sealed class ForwardTimeouts
{
    /// <summary>发出请求到第一个上游事件（含序幕）。</summary>
    public TimeSpan FirstEvent { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 图片请求的首个事件上限（Q233）。不开 <c>partial_images</c> 时第一帧就是成图，
    /// OpenAI 说复杂提示词可能要 2 分钟，60 秒会在上游已经扣费之后放弃。
    /// </summary>
    public TimeSpan ImageFirstEvent { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>流中途两个事件之间的静默上限。长思考要留足，YARP 默认的 100 秒太短。</summary>
    public TimeSpan StreamIdle { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>非流式请求的总时长。openai-python 与 anthropic 的默认超时都是 600 秒。</summary>
    public TimeSpan NonStreamTotal { get; init; } = TimeSpan.FromMinutes(10);
}
