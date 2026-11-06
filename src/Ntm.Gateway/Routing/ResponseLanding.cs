namespace Ntm.Gateway.Routing;

/// <summary>落点记的是哪种上游 id。</summary>
public enum ResponseLandingKind
{
    /// <summary>Responses 的响应 id（<c>resp_…</c>）。</summary>
    Response = 1,

    /// <summary>上游对话 id（<c>conv_…</c>），见 UpstreamConversation。</summary>
    Conversation = 2,
}

/// <summary>
/// ResponseLanding：一个上游 id 存在哪个渠道的哪把渠道密钥里（ADR-0016、ADR-0020）。
/// 带这个 id 的后续请求（previous_response_id、conversation、按 id 的端点）只能回到这里，不换候选。
/// 存进 ntm-traffic.db 的 response_landings 表，内存里留一份缓存；没命中再查库（Q269）。
/// 这个对象当快照用：改动都换新对象，不在原对象上改。
/// </summary>
public sealed class ResponseLanding
{
    public const int MaxIdLength = 200;

    /// <summary>上游给的 id，原样，不改写（Q269）。</summary>
    public required string Id { get; init; }

    public ResponseLandingKind Kind { get; init; }

    public int ChannelId { get; init; }

    /// <summary>渠道密钥值的指纹：密钥换了值就找不回去了（Q273）。</summary>
    public required string KeyFingerprint { get; init; }

    /// <summary>产出它的上游模型。上游对话不属于某个模型，为 null。</summary>
    public string? UpstreamModel { get; init; }

    /// <summary>产出它的 API 密钥，Workbench 记 0。只有同一个 API 密钥才认这个 id（Q271）。</summary>
    public int ApiKeyId { get; init; }

    /// <summary>产出它的下发模型；上游对话为 null。按 id 的端点按它检查模型白名单（Q274）。</summary>
    public string? PublishedModel { get; init; }

    /// <summary>产出它的请求记录；background 回填用量用（Q277）。上游对话为 null。</summary>
    public Guid? RequestId { get; init; }

    /// <summary>background 响应还没拿到用量：查到完成态时回填一次，之后清掉（Q277）。</summary>
    public bool AwaitingUsage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastUsedAt { get; init; }

    internal ResponseLanding Touched(DateTimeOffset now) => Copy(now, AwaitingUsage);

    internal ResponseLanding Settled() => Copy(LastUsedAt, awaitingUsage: false);

    private ResponseLanding Copy(DateTimeOffset lastUsedAt, bool awaitingUsage) => new()
    {
        Id = Id,
        Kind = Kind,
        ChannelId = ChannelId,
        KeyFingerprint = KeyFingerprint,
        UpstreamModel = UpstreamModel,
        ApiKeyId = ApiKeyId,
        PublishedModel = PublishedModel,
        RequestId = RequestId,
        AwaitingUsage = awaitingUsage,
        CreatedAt = CreatedAt,
        LastUsedAt = lastUsedAt,
    };
}
