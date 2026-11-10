using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Audit;
using Ntm.Gateway.Data;

namespace Ntm.Server.Admin;

internal sealed record AuditChangeView(string Entity, string? EntityId, string? Name, string State, IReadOnlyList<string> Fields);

internal sealed record AuditEntryView(
    long Id,
    DateTimeOffset OccurredAt,
    string? RemoteAddress,
    string Action,
    bool Succeeded,
    string? TargetType,
    string? TargetId,
    string? TargetName,
    IReadOnlyList<AuditChangeView> Changes);

internal sealed record AuditPageView(IReadOnlyList<AuditEntryView> Items, string? NextCursor);

/// <summary>操作记录页（Q358）：最新的在前，可以按动作过滤。</summary>
internal static class AuditEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapGet("/actions", ActionsAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        TrafficDbContext db,
        string? action,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        IQueryable<AuditEntry> query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(e => e.Action == action);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out long before))
            {
                return new ValidationErrors().Add("cursor", "翻页位置不对。").ToResult();
            }

            query = query.Where(e => e.Id < before);
        }

        List<AuditEntry> rows = await query
            .OrderByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? next = rows.Count > take ? rows[take - 1].Id.ToString(CultureInfo.InvariantCulture) : null;
        return TypedResults.Ok(new AuditPageView([.. rows.Take(take).Select(ToView)], next));
    }

    /// <summary>库里出现过的动作，给过滤下拉框用。</summary>
    private static async Task<IResult> ActionsAsync(TrafficDbContext db, CancellationToken cancellationToken)
    {
        List<string> actions = await db.AuditEntries
            .AsNoTracking()
            .Select(e => e.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(actions);
    }

    private static AuditEntryView ToView(AuditEntry e) => new(
        e.Id,
        e.OccurredAt,
        e.RemoteAddress,
        e.Action,
        e.Succeeded,
        e.TargetType,
        e.TargetId,
        e.TargetName,
        [.. e.Changes.Select(c => new AuditChangeView(
            c.Entity,
            c.EntityId,
            c.Name,
            c.State,
            string.IsNullOrEmpty(c.Fields) ? [] : c.Fields.Split(',')))]);
}
