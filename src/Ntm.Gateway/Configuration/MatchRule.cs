namespace Ntm.Gateway.Configuration;

/// <summary>
/// 匹配方式。显式二选一，不做 AxonHub 那种「含特殊字符就当正则」的启发式——
/// 模型名几乎都带点号，那种猜法会让 <c>gpt-4.1</c> 把 <c>gpt-4x1</c> 也匹配上（ADR-0011）。
/// </summary>
public enum MatchKind
{
    /// <summary>精确名字。内部走 <c>Regex.Escape</c>，点号就是点号。</summary>
    Exact = 1,

    /// <summary>正则。完全锚定，NonBacktracking 引擎，不支持前后查找与反向引用。</summary>
    Regex = 2,
}

/// <summary>
/// 把一个 PublishedModel 关联到若干 UpstreamModel 的规则。一条规则命中多个上游模型时，
/// 它们全部成为候选，顺序确定：先按 Channel 的 Priority 降序，再按 UpstreamModel 名字典序。
/// 同一个 (Channel, UpstreamModel) 被多条规则命中时先到先得，靠 <see cref="Ordinal"/> 定序。
/// </summary>
public sealed class MatchRule
{
    public int Id { get; set; }

    public int PublishedModelId { get; set; }

    /// <summary>规则在这个 PublishedModel 内的顺序，从 0 起。界面上可拖动调整。</summary>
    public int Ordinal { get; set; }

    public MatchKind Kind { get; set; } = MatchKind.Exact;

    /// <summary>精确名或正则模式。</summary>
    public required string Pattern { get; set; }

    /// <summary>限定只在这个 Channel 上匹配；为空表示所有 Channel。</summary>
    public int? ChannelId { get; set; }

    /// <summary>
    /// 排除这些渠道。用 ID 而不是渠道名正则：改个名字不该改变路由（Q37）。
    /// 存成 JSON 数组列。
    /// </summary>
    public List<int> ExcludedChannelIds { get; } = [];

    /// <summary>
    /// 排除匹配这些模式的上游模型名。独立字段而不是写在主模式里，
    /// 因为 NonBacktracking 不支持负向前查找（ADR-0011）。语义同 <see cref="Kind"/>。
    /// </summary>
    public List<string> ExcludedPatterns { get; } = [];

    /// <summary>覆盖 Channel 的 Priority；为空用渠道自己的。</summary>
    public int? Priority { get; set; }

    /// <summary>覆盖 Channel 的 Weight；为空用渠道自己的。</summary>
    public int? Weight { get; set; }

    public bool Enabled { get; set; } = true;

    public PublishedModel? PublishedModel { get; set; }
}
