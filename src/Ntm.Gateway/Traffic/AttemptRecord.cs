using Ntm.Protocol;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 一次 Request 里对某个候选的一次实际上游调用。换密钥、换渠道都各算一次。
/// 用量与费用记在成功的那一次上，Request 上留汇总。
/// </summary>
public sealed class AttemptRecord
{
    public long Id { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>第几次，从 1 开始。</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int ChannelId { get; set; }

    /// <summary>反范式，渠道在 ntm.db。</summary>
    public required string ChannelName { get; set; }

    public int ChannelKeyId { get; set; }

    /// <summary>密钥值的指纹。冷却与限速按它归集（Q21），记录里也按它追溯。</summary>
    public required string ChannelKeyFingerprint { get; set; }

    /// <summary>给人看的密钥前缀。</summary>
    public required string ChannelKeyPrefix { get; set; }

    /// <summary>上游给的响应 id（如 <c>resp_…</c>、<c>chatcmpl-…</c>），没有就是 null（Q286）。</summary>
    public string? ResponseId { get; set; }

    public ApiFormat UpstreamApiFormat { get; set; }

    /// <summary>上游实际服务的模型名。计价与冷却的模型作用域都用它。</summary>
    public required string UpstreamModel { get; set; }

    /// <summary>实际打到的地址。排查配置问题时不必开 DebugCapture 就能看。</summary>
    public required string RequestUri { get; set; }

    /// <summary>上游返回的状态码。连接根本没建立时为 null。</summary>
    public int? HttpStatusCode { get; set; }

    public FailureClass FailureClass { get; set; }

    /// <summary>这次失败导致了哪一层冷却。</summary>
    public CooldownScope CooldownScope { get; set; }

    public DateTimeOffset? CooldownUntil { get; set; }

    /// <summary>截断后的错误摘要。原文要开 DebugCapture。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>第一个上游事件（含序幕）到达的时刻。</summary>
    public DateTimeOffset? FirstEventAt { get; set; }

    /// <summary>第一个 ContentEvent 到达的时刻。和 FirstEventAt 的差就是序幕有多长。</summary>
    public DateTimeOffset? FirstContentEventAt { get; set; }

    /// <summary>这次是否走到了提交。提交之后的失败不能再换候选（ADR-0008）。</summary>
    public bool Committed { get; set; }

    /// <summary>入站与上游 ApiFormat 相同，走了字节透传（ADR-0002）。</summary>
    public bool Passthrough { get; set; }

    public long InputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWrite5mTokens { get; set; }

    public long CacheWrite1hTokens { get; set; }

    public long OutputTokens { get; set; }

    public UsageSource UsageSource { get; set; }

    /// <summary>
    /// 上游输出了思考过程（非空的 reasoning_content / reasoning）。为真时整个输出按思考模式的单价算，
    /// <see cref="OutputPrice"/> 记的就是那一档（ADR-0013）。
    /// </summary>
    public bool SawThinking { get; set; }

    /// <summary>当时渠道倍率的快照，万分之一（Q34）。事后改倍率不重写历史。</summary>
    public int MultiplierBasisPoints { get; set; } = 10000;

    /// <summary>纳美元，已乘倍率。缺单价时为 null。</summary>
    public long? CostNanoUsd { get; set; }

    /// <summary>
    /// 当时用的五项单价快照（Q49），美元 / 每百万 Token。事后改单价不重写历史。
    /// 全为 null 表示这次没有单价，费用也就是 null。输出单价是实际用的那一档：
    /// <see cref="SawThinking"/> 为真时是思考模式的单价（没填就退回普通输出单价）。
    /// </summary>
    public decimal? InputPrice { get; set; }

    public decimal? CacheReadPrice { get; set; }

    public decimal? CacheWrite5mPrice { get; set; }

    public decimal? CacheWrite1hPrice { get; set; }

    public decimal? OutputPrice { get; set; }

    /// <summary>用的是哪一组长上下文单价的门槛（Q66）；按基础单价算时为 null。</summary>
    public long? LongContextOver { get; set; }

    /// <summary>图片请求上游返回的张数（Q230）。别的请求是 0。</summary>
    public int ImageCount { get; set; }

    /// <summary>
    /// 按张计价时那张单价的快照，美元（Q230）。有值时费用 = 张数 × 它 × 倍率，五项 Token 单价都是 null。
    /// </summary>
    public decimal? ImagePrice { get; set; }

    public RequestRecord? Request { get; set; }
}
