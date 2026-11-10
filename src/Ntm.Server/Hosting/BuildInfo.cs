using System.Reflection;

namespace Ntm.Server.Hosting;

/// <summary>
/// 发布流程传入版本与提交（Q335/Q369）：正式版本为 X.Y.Z，手动测试镜像为 0.0.0-edge，
/// 本地默认构建为 <c>0.0.0-dev</c>。提交号来自 InformationalVersion 的 <c>+</c> 后缀。
/// </summary>
internal static class BuildInfo
{
    private static readonly (string Version, string? Commit) Parsed = Parse(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Version => Parsed.Version;

    /// <summary>构建时的提交（前 12 位）；不知道时为 null。</summary>
    public static string? Commit => Parsed.Commit;

    internal static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("0.0.0-dev", null);
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, null);
        }

        string commit = informational[(plus + 1)..];
        return (informational[..plus], commit.Length == 0 ? null : commit.Length > 12 ? commit[..12] : commit);
    }
}

internal static partial class StartupLog
{
    [LoggerMessage(EventId = 5101, Level = LogLevel.Information, Message = "南天门 {Version}（提交 {Commit}）正在启动")]
    public static partial void Starting(ILogger logger, string version, string commit);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Warning,
        Message = "/metrics 已开启但没有设置令牌（Ntm__Metrics__Token），谁都能读到渠道和用量统计。")]
    public static partial void MetricsWithoutToken(ILogger logger);
}
