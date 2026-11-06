using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Debugging;

/// <summary>
/// 这次 Attempt 录不录原文（Q155）：全局、Request 的 API 密钥、Attempt 的渠道，三处任一开着就录。
/// 「开着」= 到期时刻还没到。到期判断按时间戳，不依赖后台把开关关掉。
/// </summary>
public static class DebugSwitch
{
    public static bool IsOn(DateTimeOffset? until, DateTimeOffset now) => until is { } at && at > now;

    public static bool ShouldCapture(
        ConfigSnapshot snapshot,
        ApiKeySnapshot? apiKey,
        ChannelSnapshot channel,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(channel);

        return IsOn(snapshot.DebugCaptureUntil, now)
            || IsOn(apiKey?.DebugCaptureUntil, now)
            || IsOn(channel.DebugCaptureUntil, now);
    }
}
