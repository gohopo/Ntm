using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ntm.Gateway;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;

namespace Ntm.Server.Hosting;

/// <summary>
/// backup / restore 共用的东西：和正常启动读同一套配置（appsettings、环境变量、<c>--键=值</c>），
/// 数据目录和文件名才对得上。位置参数（目录）要先挑出来，命令行配置源不认识裸的位置参数。
/// </summary>
internal static class DataCommandSupport
{
    public const string ManifestFileName = "manifest.json";
    public const string KeysDirectoryName = "keys";

    public static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public static (string? Path, string[] Settings) Split(string[] args)
    {
        string? path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        return (path, [.. args.Where(a => a.StartsWith("--", StringComparison.Ordinal))]);
    }

    public static (NtmDataOptions Data, PricingOptions Pricing) ReadOptions(string[] settings)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(settings);
        var data = new NtmDataOptions();
        builder.Configuration.GetSection(NtmDataOptions.SectionName).Bind(data);
        var pricing = new PricingOptions();
        builder.Configuration.GetSection(PricingOptions.SectionName).Bind(pricing);
        return (data, pricing);
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public static string Stamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
}

internal sealed record BackupManifest(string Version, string? Commit, DateTimeOffset CreatedAt, IReadOnlyList<string> Files);

/// <summary>
/// <c>backup [目录]</c>：网关运行中也能做（Q340）。两个库用 SQLite 的 <c>VACUUM INTO</c> 拷一份一致的快照，
/// 再拷密钥环和价目缓存。调试库不备份——调试原文 24 小时就过期。备份里有密钥环，能解开所有渠道密钥，要当密码一样保管。
/// </summary>
internal static class BackupCommand
{
    public const string Name = "backup";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider? time = null)
    {
        (string? path, string[] settings) = DataCommandSupport.Split(args);
        (NtmDataOptions data, PricingOptions pricing) = DataCommandSupport.ReadOptions(settings);
        string source = data.ResolveDirectory();
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        string target = Path.GetFullPath(path ?? Path.Combine(source, "backups", DataCommandSupport.Stamp(now)));

        string config = Path.Combine(source, data.ConfigDatabaseFileName);
        string traffic = Path.Combine(source, data.TrafficDatabaseFileName);
        if (!File.Exists(config) || !File.Exists(traffic))
        {
            await output.WriteLineAsync($"{source} 里没有网关的数据库，检查数据目录（Ntm__Data__Directory）。").ConfigureAwait(false);
            return 1;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            await output.WriteLineAsync($"{target} 已经存在而且不是空的，换一个目录。").ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(target);
        var files = new List<string>();
        await VacuumIntoAsync(config, Path.Combine(target, data.ConfigDatabaseFileName)).ConfigureAwait(false);
        files.Add(data.ConfigDatabaseFileName);
        await VacuumIntoAsync(traffic, Path.Combine(target, data.TrafficDatabaseFileName)).ConfigureAwait(false);
        files.Add(data.TrafficDatabaseFileName);

        string keys = Path.Combine(source, DataCommandSupport.KeysDirectoryName);
        if (Directory.Exists(keys))
        {
            DataCommandSupport.CopyDirectory(keys, Path.Combine(target, DataCommandSupport.KeysDirectoryName));
            files.Add(DataCommandSupport.KeysDirectoryName + "/");
        }

        string prices = Path.Combine(source, pricing.CacheFileName);
        if (File.Exists(prices))
        {
            File.Copy(prices, Path.Combine(target, pricing.CacheFileName));
            files.Add(pricing.CacheFileName);
        }

        var manifest = new BackupManifest(BuildInfo.Version, BuildInfo.Commit, now, files);
        await File.WriteAllTextAsync(
            Path.Combine(target, DataCommandSupport.ManifestFileName),
            JsonSerializer.Serialize(manifest, DataCommandSupport.ManifestJson)).ConfigureAwait(false);

        await output.WriteLineAsync($"已备份到 {target}：{string.Join("、", files)}。").ConfigureAwait(false);
        await output.WriteLineAsync("备份里有密钥环，能解开所有渠道密钥，请妥善保管。").ConfigureAwait(false);
        return 0;
    }

    private static async Task VacuumIntoAsync(string database, string target)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// <c>restore 备份目录</c>：只能在网关停着时做（靠数据目录锁判断，Q341）。现有文件先挪进
/// <c>before-restore-时间</c>，不删；再把备份拷进来。调试库不恢复，下次启动会建一个空的。
/// </summary>
internal static class RestoreCommand
{
    public const string Name = "restore";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider? time = null)
    {
        (string? path, string[] settings) = DataCommandSupport.Split(args);
        if (path is null)
        {
            await output.WriteLineAsync("用法：restore 备份目录").ConfigureAwait(false);
            return 1;
        }

        (NtmDataOptions data, PricingOptions pricing) = DataCommandSupport.ReadOptions(settings);
        string backup = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(backup, DataCommandSupport.ManifestFileName))
            || !File.Exists(Path.Combine(backup, data.ConfigDatabaseFileName))
            || !File.Exists(Path.Combine(backup, data.TrafficDatabaseFileName)))
        {
            await output.WriteLineAsync($"{backup} 不像网关的备份：缺 {DataCommandSupport.ManifestFileName} 或数据库文件。").ConfigureAwait(false);
            return 1;
        }

        string target = data.ResolveDirectory();
        DataDirectoryLock held;
        try
        {
            held = DataDirectoryLock.Acquire(target);
        }
        catch (NtmStartupException)
        {
            await output.WriteLineAsync($"网关还在用 {target}，先停掉网关再恢复。").ConfigureAwait(false);
            return 1;
        }

        using (held)
        {
            string aside = Path.Combine(target, "before-restore-" + DataCommandSupport.Stamp((time ?? TimeProvider.System).GetUtcNow()));
            var moved = new List<string>();
            foreach (string name in DataFiles(data).Append(pricing.CacheFileName))
            {
                string file = Path.Combine(target, name);
                if (File.Exists(file))
                {
                    Directory.CreateDirectory(aside);
                    File.Move(file, Path.Combine(aside, name));
                    moved.Add(name);
                }
            }

            string keys = Path.Combine(target, DataCommandSupport.KeysDirectoryName);
            if (Directory.Exists(keys))
            {
                Directory.CreateDirectory(aside);
                Directory.Move(keys, Path.Combine(aside, DataCommandSupport.KeysDirectoryName));
                moved.Add(DataCommandSupport.KeysDirectoryName + "/");
            }

            File.Copy(Path.Combine(backup, data.ConfigDatabaseFileName), Path.Combine(target, data.ConfigDatabaseFileName));
            File.Copy(Path.Combine(backup, data.TrafficDatabaseFileName), Path.Combine(target, data.TrafficDatabaseFileName));
            string backupKeys = Path.Combine(backup, DataCommandSupport.KeysDirectoryName);
            if (Directory.Exists(backupKeys))
            {
                DataCommandSupport.CopyDirectory(backupKeys, keys);
            }

            string backupPrices = Path.Combine(backup, pricing.CacheFileName);
            if (File.Exists(backupPrices))
            {
                File.Copy(backupPrices, Path.Combine(target, pricing.CacheFileName));
            }

            if (moved.Count > 0)
            {
                await output.WriteLineAsync($"原来的 {string.Join("、", moved)} 挪到了 {aside}。").ConfigureAwait(false);
            }

            await output.WriteLineAsync($"已从 {backup} 恢复到 {target}。启动网关即可。").ConfigureAwait(false);
            return 0;
        }
    }

    /// <summary>三个库连同 WAL 的两个伴随文件。</summary>
    private static IEnumerable<string> DataFiles(NtmDataOptions data)
    {
        foreach (string database in new[] { data.ConfigDatabaseFileName, data.TrafficDatabaseFileName, data.DebugDatabaseFileName })
        {
            yield return database;
            yield return database + "-wal";
            yield return database + "-shm";
        }
    }
}
