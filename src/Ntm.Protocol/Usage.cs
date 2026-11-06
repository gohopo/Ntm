namespace Ntm.Protocol;

/// <summary>Usage 的来源。记录里要能区分精确值和估算值（Q20）。</summary>
public enum UsageSource
{
    /// <summary>嗅探失败或上游没给，没有可用的用量。</summary>
    Unavailable = 0,

    /// <summary>上游给出的精确值。</summary>
    Upstream = 1,

    /// <summary>客户端断开后由网关按已观察到的内容估算。</summary>
    GatewayEstimated = 2,
}

/// <summary>
/// 一次上游调用消耗的 LLM 计量，五项互不重叠（ADR-0005）。
/// 不沿用 OpenAI「<c>prompt_tokens</c> 含缓存」的约定：那个约定逼着每个非 OpenAI 适配器
/// 在边缘把缓存项加进去再减出来。换算只在解析上游响应时做一次。
/// </summary>
public readonly record struct Usage
{
    /// <summary>没有命中缓存的输入。</summary>
    public long InputTokens { get; init; }

    /// <summary>从缓存读到的输入。</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>写入 5 分钟缓存的输入。</summary>
    public long CacheWrite5mTokens { get; init; }

    /// <summary>写入 1 小时缓存的输入。</summary>
    public long CacheWrite1hTokens { get; init; }

    /// <summary>输出，含推理内容。</summary>
    public long OutputTokens { get; init; }

    public UsageSource Source { get; init; }

    public static Usage Unavailable => default;

    /// <summary>有没有拿到任何计量。</summary>
    public bool HasValue => Source != UsageSource.Unavailable;

    /// <summary>五项输入之和。计费不用它，只给展示与排错。</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWrite5mTokens + CacheWrite1hTokens;
}
