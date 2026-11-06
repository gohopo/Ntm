namespace Ntm.Gateway.Configuration;

/// <summary>
/// 全局设置。只有一行（<see cref="SingletonId"/>），需要新设置时加列（Q154）。
/// </summary>
public class GatewaySettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// 全局 DebugCapture 的到期时刻；null 或已过去都表示关。开关和到期时间合成一个值，
    /// 省掉「开着但已过期」这种自相矛盾的状态（Q154/Q155）。
    /// </summary>
    public DateTimeOffset? DebugCaptureUntil { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
