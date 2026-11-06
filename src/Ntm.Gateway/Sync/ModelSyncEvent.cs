namespace Ntm.Gateway.Sync;

public enum ModelSyncEventKind
{
    /// <summary>新同步到的 UpstreamModel 正好被已有的 MatchRule 匹配上，开始接流量（Q39/Q140）。</summary>
    EnteredRouting = 1,

    /// <summary>上游不再列出的 UpstreamModel 被删掉，而它之前在选路里。</summary>
    LeftRouting = 2,
}

/// <summary>
/// ModelSync 让选路发生变化时记一条，给面板提醒用（Q140）。只记这两种：
/// 清单变了但没有影响选路的不值得打扰人。保留 30 天。
/// </summary>
public sealed class ModelSyncEvent
{
    public const int MaxPublishedModelsLength = 1000;

    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public ModelSyncEventKind Kind { get; set; }

    public int ChannelId { get; set; }

    /// <summary>当时的渠道名。渠道删了或改名之后，事件照样看得懂。</summary>
    public required string ChannelName { get; set; }

    public required string UpstreamModel { get; set; }

    /// <summary>受影响的 PublishedModel 名，逗号分隔。</summary>
    public required string PublishedModels { get; set; }

    /// <summary>Admin 点过「知道了」。只影响总览上的提醒，不删事件。</summary>
    public bool Acknowledged { get; set; }
}
