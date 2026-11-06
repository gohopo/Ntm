namespace Ntm.Gateway.Data;

/// <summary>三个 SQLite 库与 Data Protection 密钥环都放在同一个数据目录下（Q33），一起备份。</summary>
public sealed class NtmDataOptions
{
    public const string SectionName = "Ntm:Data";

    /// <summary>
    /// 数据目录。必须是本地盘：WAL 在网络文件系统上不工作。
    /// </summary>
    public string Directory { get; set; } = "data";

    /// <summary>配置库文件名。</summary>
    public string ConfigDatabaseFileName { get; set; } = "ntm.db";

    /// <summary>流量库文件名。故意不叫 logs——它是账（ADR-0004）。</summary>
    public string TrafficDatabaseFileName { get; set; } = "ntm-traffic.db";

    /// <summary>调试原文库文件名。单独一个库，好清、好 VACUUM（Q153）。</summary>
    public string DebugDatabaseFileName { get; set; } = "ntm-debug.db";

    public string ResolveDirectory() => Path.GetFullPath(Directory);

    public string ConfigConnectionString =>
        BuildConnectionString(Path.Combine(ResolveDirectory(), ConfigDatabaseFileName));

    public string TrafficConnectionString =>
        BuildConnectionString(Path.Combine(ResolveDirectory(), TrafficDatabaseFileName));

    public string DebugConnectionString =>
        BuildConnectionString(Path.Combine(ResolveDirectory(), DebugDatabaseFileName));

    /// <summary>
    /// 不开 <c>Cache=Shared</c>：它和 WAL 配在一起会退化并发表现。
    /// busy 重试交给 Microsoft.Data.Sqlite 自己的 Default Timeout（30 秒）。
    /// </summary>
    private static string BuildConnectionString(string path) => $"Data Source={path}";
}
