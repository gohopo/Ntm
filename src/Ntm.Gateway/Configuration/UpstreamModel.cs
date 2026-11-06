namespace Ntm.Gateway.Configuration;

/// <summary>这条 UpstreamModel 记录是怎么来的。</summary>
public enum UpstreamModelSource
{
    /// <summary>人工录入。不会被同步覆盖——有些中转和自建上游根本没有 /models 端点。</summary>
    Manual = 1,

    /// <summary>从上游的模型列表同步来的。</summary>
    Synced = 2,
}

/// <summary>
/// 某个 Channel 上游实际提供的一个模型名。它是 MatchRule 的匹配对象，也是写进上游请求里的那个名字。
/// 渠道没有列出这个名字，规则就匹配不到它——清单是唯一的事实来源，不存在「规则凭空命中一个没列出的模型」。
/// </summary>
public sealed class UpstreamModel
{
    public int Id { get; set; }

    public int ChannelId { get; set; }

    /// <summary>上游认的模型名。同一渠道内唯一，大小写敏感——上游就是大小写敏感的。</summary>
    public required string Name { get; set; }

    public UpstreamModelSource Source { get; set; } = UpstreamModelSource.Manual;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近一次同步确认它还在的时间。人工录入的为空。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public Channel? Channel { get; set; }
}
