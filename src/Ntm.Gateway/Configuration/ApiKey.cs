namespace Ntm.Gateway.Configuration;

/// <summary>
/// 客户端用来访问 Ntm 的凭证。只存不可逆的指纹，按指纹查（ADR-0006）：
/// 完整值只在创建时显示一次，之后只看得到前缀。它从不出现在对上游的请求里。
/// </summary>
public sealed class ApiKey
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>API 密钥值的不可逆指纹。查询按它走。</summary>
    public required string Fingerprint { get; set; }

    /// <summary>给界面看的前缀。</summary>
    public required string Prefix { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>费用上限，纳美元（ADR-0009）。null 表示不限。按乘过渠道倍率的 Cost 算（Q34），终身累计、不按天重置（Q77）。</summary>
    public long? CostLimitNanoUsd { get; set; }

    /// <summary>同时最多几个进行中的 Request，null 表示不限（Q80）。</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>能用的 PublishedModel 名；空表示全部（Q83）。</summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>只走这些渠道；空表示不限（Q83）。先按它过滤，再减去 <see cref="BlockedChannelIds"/>。</summary>
    public List<int> AllowedChannelIds { get; set; } = [];

    /// <summary>不走这些渠道（Q83）。</summary>
    public List<int> BlockedChannelIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// 这个 API 密钥的 DebugCapture 到期时刻；null 或已过去都表示关。和全局、渠道上的开关取或（Q155）。
    /// </summary>
    public DateTimeOffset? DebugCaptureUntil { get; set; }
}
