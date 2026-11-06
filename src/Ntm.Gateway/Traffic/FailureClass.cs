namespace Ntm.Gateway.Traffic;

/// <summary>
/// 一次 Attempt 的失败归类。它决定冷却的作用域与初始退避时长（Q28、Q29）。
/// Protocol 只报告它看到了什么（HTTP 状态、错误载荷、流是否空），归类在 Gateway。
/// </summary>
public enum FailureClass
{
    /// <summary>没失败。</summary>
    None = 0,

    /// <summary>401/403，或流内 authentication_error。冷却这把密钥。</summary>
    AuthFailure = 1,

    /// <summary>402、余额不足、额度用尽。冷却这把密钥。</summary>
    QuotaExhausted = 2,

    /// <summary>429，或流内 rate_limit_error。冷却「这把密钥 + 这个模型」。</summary>
    RateLimited = 3,

    /// <summary>404 model_not_found。冷却「这个渠道 + 这个模型」。</summary>
    ModelNotFound = 4,

    /// <summary>上游明说自己过载（overloaded_error、service_unavailable_error）。冷却「渠道 + 模型」。</summary>
    UpstreamOverloaded = 5,

    /// <summary>上游 5xx。冷却「渠道 + 模型」。</summary>
    UpstreamServerError = 6,

    /// <summary>发出请求后迟迟没有第一个上游事件。冷却「渠道 + 模型」。</summary>
    FirstEventTimeout = 7,

    /// <summary>流中途静默超时。冷却「渠道 + 模型」。</summary>
    IdleTimeout = 8,

    /// <summary>DNS、拒绝连接、TLS、连接超时、Cloudflare 质询页。冷却整个渠道。</summary>
    ConnectionFailure = 9,

    /// <summary>HTTP 200 但响应体是错误（软错误）。按解析出的错误类型再细分，默认按「渠道 + 模型」。</summary>
    SoftError = 10,

    /// <summary>400/413/422：请求本身不对。不冷却、不换候选，原样返回客户端。</summary>
    ClientRequestError = 11,

    /// <summary>客户端断开。不冷却，不计入渠道健康（Q20）。</summary>
    ClientDisconnected = 12,

    /// <summary>Admin 在日志页中断（Interrupt，Q15/Q115）。不换候选，不冷却，不计入渠道健康。旧名 ManuallyAborted，库里存的数字不变。</summary>
    Interrupted = 13,

    /// <summary>认不出来。保守按「渠道 + 模型」冷却。</summary>
    Unknown = 14,

    /// <summary>
    /// 流已经开始输出，但上游在给出结束标记前断开。冷却「渠道 + 模型」——
    /// 这不是传输层故障（那是 <see cref="ConnectionFailure"/>，冷却整个渠道），
    /// 通常是某个模型的生成过程挂了，同渠道其他模型照常可用。
    /// </summary>
    StreamInterrupted = 15,

    /// <summary>
    /// API 密钥的费用上限用完了，网关自己拒绝，一次也没打上游（Q85）。不进 Cooldown，不计健康度；
    /// 和上游说的额度用尽 <see cref="QuotaExhausted"/> 分开，免得面板上分不清是谁没钱了。
    /// </summary>
    QuotaBlocked = 16,

    /// <summary>被 Restriction 挡住（模型限制、并发上限、渠道的每日费用 / RPM / 并发 / 时段），网关自己拒绝（Q85）。不进 Cooldown，不计健康度。</summary>
    RestrictionBlocked = 17,
}
