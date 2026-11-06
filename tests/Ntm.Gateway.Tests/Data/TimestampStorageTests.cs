using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Data;

/// <summary>
/// 时间戳按 Unix 毫秒整数存（ADR-0012）。这组测试证明的是：不转换时 SQLite 里的
/// DateTimeOffset 排序不是时间顺序，而请求记录必须能在 SQL 里按时间排序和过滤。
/// </summary>
public sealed class TimestampStorageTests
{
    /// <summary>东八区的 22:00 早于 UTC 的 15:30，但按带偏移量的文本比较会排在后面。</summary>
    private static readonly DateTimeOffset EarlierInShanghai =
        new(2026, 10, 25, 22, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset LaterInUtc =
        new(2026, 10, 25, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ShanghaiTimestampIsActuallyEarlier()
    {
        Assert.True(EarlierInShanghai < LaterInUtc);
    }

    [Fact]
    public async Task RawDateTimeOffsetTextDoesNotSortChronologically()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (label TEXT, at TEXT)";
            await create.ExecuteNonQueryAsync();
        }

        // EF 的 SQLite 提供程序就是把 DateTimeOffset 存成这个格式的文本。
        await Insert(connection, "shanghai", EarlierInShanghai.ToString("yyyy-MM-dd HH:mm:sszzz", null));
        await Insert(connection, "utc", LaterInUtc.ToString("yyyy-MM-dd HH:mm:sszzz", null));

        List<string> order = await ReadLabelsOrderedByAt(connection);

        // 时间上在前的那条，按文本排却在后：ORDER BY 给出的不是时间顺序。
        Assert.Equal(["utc", "shanghai"], order);
    }

    [Fact]
    public async Task UnixMillisecondsSortChronologically()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (label TEXT, at INTEGER)";
            await create.ExecuteNonQueryAsync();
        }

        await Insert(connection, "shanghai", EarlierInShanghai.ToUnixTimeMilliseconds());
        await Insert(connection, "utc", LaterInUtc.ToUnixTimeMilliseconds());

        List<string> order = await ReadLabelsOrderedByAt(connection);

        Assert.Equal(["shanghai", "utc"], order);
    }

    [Fact]
    public async Task TrafficContextOrdersAndFiltersInSql()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TrafficDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new TrafficDbContext(options);
        await context.Database.EnsureCreatedAsync();

        context.Requests.Add(NewRequest("shanghai", EarlierInShanghai));
        context.Requests.Add(NewRequest("utc", LaterInUtc));
        await context.SaveChangesAsync();

        // 排序在 SQL 里做。
        List<string> ordered = await context.Requests
            .OrderBy(r => r.StartedAt)
            .Select(r => r.PublishedModel)
            .ToListAsync();

        Assert.Equal(["shanghai", "utc"], ordered);

        // 区间过滤也在 SQL 里做：保留期删除要靠它。
        DateTimeOffset cutoff = EarlierInShanghai.AddMinutes(1);
        List<string> after = await context.Requests
            .Where(r => r.StartedAt >= cutoff)
            .Select(r => r.PublishedModel)
            .ToListAsync();

        Assert.Equal(["utc"], after);
    }

    [Fact]
    public async Task RoundTripKeepsTheInstant()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TrafficDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new TrafficDbContext(options);
        await context.Database.EnsureCreatedAsync();

        context.Requests.Add(NewRequest("shanghai", EarlierInShanghai));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        RequestRecord loaded = await context.Requests.SingleAsync();

        // 偏移量不保留——存的是瞬间。时间点必须完全一致。
        Assert.Equal(EarlierInShanghai.ToUnixTimeMilliseconds(), loaded.StartedAt.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.Zero, loaded.StartedAt.Offset);
    }

    private static RequestRecord NewRequest(string label, DateTimeOffset startedAt) => new()
    {
        Id = Guid.CreateVersion7(startedAt),
        StartedAt = startedAt,
        PublishedModel = label,
        InboundApiFormat = ApiFormat.OpenAiChat,
        RequestKind = RequestKind.Chat,
        Status = RequestStatus.Running,
    };

    private static async Task Insert(SqliteConnection connection, string label, object at)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t (label, at) VALUES ($label, $at)";
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$at", at);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadLabelsOrderedByAt(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT label FROM t ORDER BY at";

        var labels = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            labels.Add(reader.GetString(0));
        }

        return labels;
    }
}
