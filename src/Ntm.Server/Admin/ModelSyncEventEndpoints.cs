using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Data;
using Ntm.Gateway.Sync;

namespace Ntm.Server.Admin;

internal sealed record ModelSyncEventView(
    long Id,
    DateTimeOffset OccurredAt,
    ModelSyncEventKind Kind,
    int ChannelId,
    string ChannelName,
    string UpstreamModel,
    string PublishedModels,
    bool Acknowledged);

/// <summary>
/// <see cref="UnacknowledgedEntered"/> 是总览页提示用的数（Q140）：「N 个新上游模型进入了选路」。
/// 离开选路的事件也列出来，但不催人看。
/// </summary>
internal sealed record ModelSyncEventListView(IReadOnlyList<ModelSyncEventView> Events, int UnacknowledgedEntered);

/// <summary>/api/model-sync-events：ModelSync 改变选路的记录，保留 30 天。</summary>
internal static class ModelSyncEventEndpoints
{
    private const int MaxListed = 500;

    public static RouteGroupBuilder MapModelSyncEventEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapPost("/acknowledge", AcknowledgeAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(ConfigDbContext db, CancellationToken cancellationToken)
    {
        List<ModelSyncEvent> events = await db.ModelSyncEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(MaxListed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int unacknowledged = await db.ModelSyncEvents
            .CountAsync(e => !e.Acknowledged && e.Kind == ModelSyncEventKind.EnteredRouting, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new ModelSyncEventListView(
            [.. events.Select(e => new ModelSyncEventView(
                e.Id, e.OccurredAt, e.Kind, e.ChannelId, e.ChannelName, e.UpstreamModel, e.PublishedModels, e.Acknowledged))],
            unacknowledged));
    }

    /// <summary>「知道了」：把现在所有没确认的事件一次确认掉。</summary>
    private static async Task<IResult> AcknowledgeAsync(ConfigDbContext db, CancellationToken cancellationToken)
    {
        await db.ModelSyncEvents
            .Where(e => !e.Acknowledged)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Acknowledged, true), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}
