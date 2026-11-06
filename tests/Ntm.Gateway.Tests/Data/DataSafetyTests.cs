using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Tests.Data;

/// <summary>一个数据目录只能有一个网关（Q341）；库比程序新时拒绝启动（Q342）。</summary>
public sealed class DataSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ntm-data-safety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ADataDirectoryCanOnlyBeHeldOnceAtATime()
    {
        using (DataDirectoryLock first = DataDirectoryLock.Acquire(_directory))
        {
            NtmStartupException refused = Assert.Throws<NtmStartupException>(() => DataDirectoryLock.Acquire(_directory));
            Assert.Contains("另一个网关", refused.Message, StringComparison.Ordinal);
        }

        // 放掉之后马上又能拿到：锁文件留着没关系，锁的是打开它的句柄。
        using DataDirectoryLock again = DataDirectoryLock.Acquire(_directory);
    }

    [Fact]
    public async Task ADatabaseWrittenByANewerVersionRefusesToStartAndIsLeftUntouched()
    {
        await using (ServiceProvider services = Services())
        {
            await services.MigrateNtmDatabasesAsync();
        }

        string traffic = Path.Combine(_directory, "ntm-traffic.db");
        await using (var connection = new SqliteConnection($"Data Source={traffic};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '10.0.0')";
            await insert.ExecuteNonQueryAsync();
        }

        await using ServiceProvider again = Services();
        NtmStartupException refused = await Assert.ThrowsAsync<NtmStartupException>(() => again.MigrateNtmDatabasesAsync());
        Assert.Contains("请求记录库", refused.Message, StringComparison.Ordinal);
        Assert.Contains("29991231000000_FromTheFuture", refused.Message, StringComparison.Ordinal);
        Assert.Contains("restore", refused.Message, StringComparison.Ordinal);
    }

    private ServiceProvider Services()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ntm:Data:Directory"] = _directory })
            .Build();
        return new ServiceCollection().AddLogging().AddNtmData(configuration).BuildServiceProvider();
    }
}
