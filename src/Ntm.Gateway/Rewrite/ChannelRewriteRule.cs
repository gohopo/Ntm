namespace Ntm.Gateway.Rewrite;

/// <summary>改写哪一部分。</summary>
public enum RewriteTarget
{
    /// <summary>发给上游的请求头。</summary>
    Header = 1,

    /// <summary>发给上游的 JSON 请求体。</summary>
    Body = 2,
}

/// <summary>怎么改。</summary>
public enum RewriteOperation
{
    /// <summary>写上这个值，客户端给过也覆盖。</summary>
    Set = 1,

    /// <summary>去掉这个字段或请求头。</summary>
    Remove = 2,

    /// <summary>客户端没给的时候才写（Q144）。</summary>
    Default = 3,
}

/// <summary>
/// 一个 Channel 上的一条请求改写规则（Q144–Q151）。存成 Channel 的 JSON 列。
/// </summary>
public sealed class ChannelRewriteRule
{
    public const int MaxPathLength = 200;
    public const int MaxValueLength = 2000;
    public const int MaxRulesPerChannel = 20;

    public RewriteTarget Target { get; set; }

    public RewriteOperation Operation { get; set; }

    /// <summary>请求头名，或请求体里以点号分隔的路径（如 <c>thinking.type</c>）。</summary>
    public required string Path { get; set; }

    /// <summary>
    /// 请求头的值，或请求体字段的 JSON 字面量。<see cref="RewriteOperation.Remove"/> 时为 null。
    /// </summary>
    public string? Value { get; set; }
}
