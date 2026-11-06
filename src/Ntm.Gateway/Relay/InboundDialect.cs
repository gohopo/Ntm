using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Protocol;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 网关自己产生的错误的种类。状态码由中继决定，各入站格式只决定错误长什么样，
/// 这样同一种失败对两种客户端的状态码一致，只是错误体换成各自 SDK 认得的形状。
/// </summary>
public enum GatewayErrorKind
{
    MissingApiKey = 1,
    InvalidApiKey = 2,
    BodyTooLarge = 3,
    InvalidRequest = 4,
    NoChannel = 5,
    BadChannelConfig = 6,
    RateLimited = 7,
    Timeout = 8,
    UpstreamFailed = 9,
    UpstreamInterrupted = 10,

    /// <summary>客户端要的模型没有下发，或者下发的不是这类请求。</summary>
    ModelNotFound = 11,

    /// <summary>这个模型的候选全在冷却。和「没有渠道」不同：稍后再来是有意义的。</summary>
    AllCooling = 12,

    /// <summary>API 密钥的费用上限用完了（Q78）。</summary>
    QuotaExhausted = 13,

    /// <summary>API 密钥不许用这个模型（Q83）。</summary>
    ModelNotAllowed = 14,

    /// <summary>
    /// Admin 中断了这个 Request（Q116）。状态码是 403：SDK 会自动重试 408/409/429/5xx，重试会再打一次上游。
    /// </summary>
    Interrupted = 15,

    /// <summary>按 id 找的 Responses 资源本网关不认识（Q272）：没产出过、过了保留期，或不是这个 API 密钥的。</summary>
    ResourceNotFound = 16,

    /// <summary>资源所在的渠道不在这个 API 密钥的渠道白名单里，或在黑名单里（Q274）。</summary>
    ChannelNotAllowed = 17,

    /// <summary>网关正在关闭，等够了还没结束的请求被切掉（Q343）。客户端可以重试，所以是 503。</summary>
    ShuttingDown = 18,
}

/// <summary>
/// 客户端说的那种 API 格式。中继的骨架（认证、选候选、逐个 Attempt、提交点、记录）与格式无关，
/// 不同的只有三件事：怎么读请求、给某个上游发什么字节、错误长什么样。
/// </summary>
public abstract class InboundDialect
{
    public static InboundDialect OpenAiChat { get; } = new OpenAiChatDialect();

    public static InboundDialect Anthropic { get; } = new AnthropicDialect();

    public static InboundDialect OpenAiResponses { get; } = new ResponsesDialect(compact: false);

    /// <summary><c>/v1/responses/compact</c>：只有 Responses 上游能做，照常选路、记账（Q283）。</summary>
    public static InboundDialect ResponsesCompact { get; } = new ResponsesDialect(compact: true);

    /// <summary><c>/v1/embeddings</c>：只在 OpenAI 格式之间透传（Q220/Q221）。</summary>
    public static InboundDialect Embeddings { get; } = new OpenAiPassthroughDialect(RequestKind.Embedding);

    /// <summary><c>/v1/images/generations</c>：只在 OpenAI 格式之间透传（Q220/Q221）。</summary>
    public static InboundDialect Images { get; } = new OpenAiPassthroughDialect(RequestKind.Image);

    public abstract ApiFormat Format { get; }

    /// <summary>这个端点接的是哪类请求。只有对话类经 IR 转换。</summary>
    public virtual RequestKind Kind => RequestKind.Chat;

    /// <summary>接在上游请求路径后面的那一段，如 compact 的 <c>/compact</c>。大多数端点没有。</summary>
    public virtual string? UpstreamPathSuffix => null;

    /// <summary>
    /// 上游返回的响应 id 要不要记成 ResponseLanding（ADR-0016、ADR-0020）。
    /// 只有 Responses 的 <c>/v1/responses</c> 记；compact 的产物不记（Q283）。
    /// </summary>
    public virtual bool RemembersResponseLandings => false;

    /// <summary>
    /// 上游对客户端请求本身的拒绝（400/413/422）是否原样转给客户端。只在这次尝试是同格式透传时生效：
    /// 转换时上游的错误是另一种格式，中继取出消息重新包装。
    /// </summary>
    public abstract bool EchoesUpstreamClientErrors { get; }

    public abstract InboundParseResult Parse(byte[] body);

    public abstract byte[] ErrorBody(GatewayErrorKind kind, string message);

    /// <summary>流内错误帧：响应头已经发出、状态码改不了时只能这样告知客户端（ADR-0008）。</summary>
    public abstract byte[] ErrorFrame(GatewayErrorKind kind, string message);
}

public readonly record struct InboundParseResult(InboundRequest? Request, string? Error);

/// <summary>读好的一个入站请求。持有该格式自己的解析结果，按候选生成要发给上游的字节。</summary>
public abstract class InboundRequest
{
    /// <summary>客户端请求的模型名（将来是 PublishedModel）。</summary>
    public required string Model { get; init; }

    public required bool IsStream { get; init; }

    /// <summary>
    /// 接着上一次 Responses 响应（ADR-0016）。只有 Responses 入站才有；有值时只能回到产生它的渠道和渠道密钥。
    /// </summary>
    public virtual string? PreviousResponseId => null;

    /// <summary>
    /// 请求带着的 <c>conversation</c>（UpstreamConversation 的 id，Q278）。有值时只能回到建它的渠道和渠道密钥。
    /// </summary>
    public virtual string? UpstreamConversationId => null;

    /// <summary><c>background: true</c>（Q276）：上游先回 queued，用量要等之后查到才有（Q277）。</summary>
    public virtual bool IsBackground => false;

    /// <summary>
    /// 转给 Responses 上游时写进 <c>prompt_cache_key</c> 的值：Conversation 的身份（Q192）。
    /// 中继在选路前算好填进来；透传不用它，客户端自己写的原样保留。
    /// </summary>
    public string? PromptCacheKey { get; set; }

    /// <summary>
    /// 这个请求能不能由这种 ApiFormat 的上游来服务。不只看格式：有的参数在别的格式里
    /// 没有等价物，带着它的请求要绕开那个候选，<paramref name="reason"/> 说明原因（ADR-0014）。
    /// </summary>
    public abstract bool CanBeServedBy(ApiFormat upstream, out string? reason);

    /// <summary>
    /// 给某个候选上游的发送计划。切换候选会重发，所以同一个上游模型的字节应该只算一次。
    /// <paramref name="thinkingStyle"/> 不为空时，转成 Anthropic 用它代替渠道的思考参数写法（Q315：换写法重发、或用记住的写法）。
    /// </summary>
    public abstract UpstreamPlan PlanFor(ChannelSnapshot channel, string upstreamModel, Guid requestId, Protocol.Anthropic.AnthropicThinkingStyle? thinkingStyle = null);
}

public sealed class UpstreamPlan
{
    public required ReadOnlyMemory<byte> Body { get; init; }

    public bool SwallowUsageOnlyFrames { get; init; }

    public ResponseConversion? Conversion { get; init; }

    /// <summary>
    /// 网关替客户端写的思考参数被上游以「这个模型不支持这种写法」拒绝时，换成这个写法重发一次（Q315）。
    /// 只有渠道是自动写法、改写规则没碰 thinking 和 output_config、而且确实写了思考时才有。
    /// </summary>
    public Protocol.Anthropic.AnthropicThinkingStyle? ThinkingFallback { get; init; }

    /// <summary>这次写的思考类型：<c>enabled</c> 或 <c>adaptive</c>。用来认上游的拒绝。</summary>
    public string? ThinkingType { get; init; }
}
