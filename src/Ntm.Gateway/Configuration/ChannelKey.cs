namespace Ntm.Gateway.Configuration;

/// <summary>
/// Channel 用来向上游证明身份的凭证。同一个 Channel 下的多个 ChannelKey 互为备份。
/// 密钥值用 Data Protection 加密后落库（ADR-0006），因为它必须原样发给上游。
/// </summary>
public sealed class ChannelKey
{
    public int Id { get; set; }

    public int ChannelId { get; set; }

    /// <summary>界面上的标识，同一 Channel 内唯一。</summary>
    public required string Name { get; set; }

    /// <summary>Data Protection 保护后的密钥值。</summary>
    public required string ProtectedSecret { get; set; }

    /// <summary>
    /// 密钥值的指纹。Cooldown 与限速计数按它归集，不按行 ID——
    /// 同一份密钥值配到多个 Channel 时，上游是按那一份密钥限流的（Q21）。
    /// </summary>
    public required string SecretFingerprint { get; set; }

    /// <summary>给界面看的前缀，便于认出是哪一把，完整值创建后不再显示。</summary>
    public required string SecretPrefix { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>档内优先级。高的先用尽才轮到低的。</summary>
    public int Priority { get; set; }

    /// <summary>每分钟最多几次 Attempt，null 表示不限。按密钥指纹计（Q21/Q79），超了这把密钥暂时不用，不进入 Cooldown。</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>同时最多几个进行中的 Attempt，null 表示不限。按密钥指纹计（Q80）。</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>这把密钥能打的 UpstreamModel 名，精确匹配；空表示全部（Q82）。</summary>
    public List<string> AllowedModels { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Channel? Channel { get; set; }
}
