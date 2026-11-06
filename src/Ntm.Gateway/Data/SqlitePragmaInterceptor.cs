using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ntm.Gateway.Data;

/// <summary>
/// 连接串没有 journal_mode / synchronous / foreign_keys 这些关键字，只能在连接打开后执行 PRAGMA。
/// WAL 让读不阻塞写；synchronous=NORMAL 掉电可能丢最后几个提交但库保持一致（ccLoad 没设，于是每次提交都 fsync）。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(DbConnection connection)
    {
        if (IsReadOnly(connection))
        {
            return;
        }

        using DbCommand command = connection.CreateCommand();
        command.CommandText = PragmaScript;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (IsReadOnly(connection))
        {
            return;
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = PragmaScript;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// EF 判断库在不在时开的是只读连接。库还不是 WAL（比如刚从 VACUUM INTO 的备份恢复）时，
    /// 在只读连接上改 journal_mode 会报 "attempt to write a readonly database"。只读连接不需要这些设置。
    /// </summary>
    private static bool IsReadOnly(DbConnection connection) =>
        connection is SqliteConnection sqlite
        && new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode == SqliteOpenMode.ReadOnly;

    private const string PragmaScript = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        """;
}
