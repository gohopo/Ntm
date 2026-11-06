using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ntm.Server.Admin;

/// <summary>列表页上的开关：只改启用状态，不必把整个对象再提交一遍。</summary>
internal sealed record EnabledInput(bool? Enabled);

/// <summary>管理接口共用的上限、返回值和保存。</summary>
internal static class AdminResults
{
    public const int MinPriority = -1000;
    public const int MaxPriority = 1000;
    public const int MinWeight = 1;
    public const int MaxWeight = 1000;

    public static IResult NotFound(string what) =>
        TypedResults.Problem(title: what + "不存在。", statusCode: StatusCodes.Status404NotFound);

    public static IResult Conflict(string message) =>
        TypedResults.Problem(title: message, statusCode: StatusCodes.Status409Conflict);

    public static IResult MissingEnabled() =>
        new ValidationErrors().Add("enabled", "请给出 enabled。").ToResult();

    /// <summary>
    /// 保存；撞上唯一约束时返回 false。重名事先查过，这里只兜两个请求同时提交的底。
    /// </summary>
    public static async Task<bool> TrySaveAsync(DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            // 2067 = SQLITE_CONSTRAINT_UNIQUE，1555 = SQLITE_CONSTRAINT_PRIMARYKEY。
            return false;
        }
    }

    /// <summary>列表里某一项的字段名前缀，例如 <c>rules[2]</c>。</summary>
    public static string Item(string list, int index) =>
        list + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    public static string Location(string collection, int id) =>
        collection + "/" + id.ToString(CultureInfo.InvariantCulture);
}
