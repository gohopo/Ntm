using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Debugging;

namespace Ntm.Server.Admin;

/// <summary>一处 DebugCapture 开关的状态：到期时刻，null 表示关（Q154）。</summary>
public sealed record DebugSwitchView(DateTimeOffset? Until);

/// <summary>全局开关，另附写盘跟不上丢掉的份数（Q160）与时长上限。</summary>
public sealed record DebugCaptureStatusView(
    DateTimeOffset? Until,
    long Dropped,
    int DefaultDurationMinutes,
    int MaxDurationMinutes,
    int RetentionHours);

/// <summary>打开开关时给多少分钟；0 表示关。</summary>
public sealed record DebugSwitchInput(int? Minutes);

/// <summary>一份调试原文。</summary>
public sealed record DebugPayloadView(
    int AttemptOrdinal,
    DebugPayloadKind Kind,
    DateTimeOffset CapturedAt,
    int? HttpStatusCode,
    string Headers,
    string Body,
    bool Truncated,
    int ByteCount);

/// <summary>
/// DebugCapture 的开关与原文（Q154–Q162）。三处开关都是「开到某个时刻」，没有永久开着的选项，
/// 打开时必须给时长，默认 1 小时，最长 24 小时。
/// </summary>
public static class DebugCaptureEndpoints
{
    /// <summary>挂在 /api/settings 下：全局开关。</summary>
    public static RouteGroupBuilder MapDebugSettingsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/debug-capture", GetGlobalAsync);
        group.MapPut("/debug-capture", SetGlobalAsync);
        return group;
    }

    /// <summary>挂在 /api/channels 下。</summary>
    public static RouteGroupBuilder MapChannelDebugEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:int}/debug-capture", GetChannelAsync);
        group.MapPut("/{id:int}/debug-capture", SetChannelAsync);
        return group;
    }

    /// <summary>挂在 /api/api-keys 下。</summary>
    public static RouteGroupBuilder MapApiKeyDebugEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:int}/debug-capture", GetApiKeyAsync);
        group.MapPut("/{id:int}/debug-capture", SetApiKeyAsync);
        return group;
    }

    /// <summary>挂在 /api/requests 下：一个 Request 的全部原文。</summary>
    public static RouteGroupBuilder MapRequestDebugEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/debug", GetPayloadsAsync);
        return group;
    }

    private static async Task<IResult> GetGlobalAsync(
        ConfigDbContext db,
        DebugCaptureSink sink,
        IOptions<DebugCaptureOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        GatewaySettings? settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == GatewaySettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(Status(settings?.DebugCaptureUntil, sink, options.Value, timeProvider.GetUtcNow()));
    }

    private static async Task<IResult> SetGlobalAsync(
        DebugSwitchInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        DebugCaptureSink sink,
        IOptions<DebugCaptureOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!TryResolve(input, options.Value, now, out DateTimeOffset? until, out IResult? problem))
        {
            return problem!;
        }

        GatewaySettings? settings = await db.Settings
            .FirstOrDefaultAsync(s => s.Id == GatewaySettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (settings is null)
        {
            settings = new GatewaySettings();
            db.Settings.Add(settings);
        }

        settings.DebugCaptureUntil = until;
        settings.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(Status(until, sink, options.Value, now));
    }

    private static async Task<IResult> GetChannelAsync(
        int id,
        ConfigDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return channel is null
            ? AdminResults.NotFound("渠道")
            : TypedResults.Ok(View(channel.DebugCaptureUntil, timeProvider.GetUtcNow()));
    }

    private static async Task<IResult> SetChannelAsync(
        int id,
        DebugSwitchInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        IOptions<DebugCaptureOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!TryResolve(input, options.Value, now, out DateTimeOffset? until, out IResult? problem))
        {
            return problem!;
        }

        // 不动 UpdatedAt：调试开关不是渠道配置的改动。
        channel.DebugCaptureUntil = until;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(View(until, now));
    }

    private static async Task<IResult> GetApiKeyAsync(
        int id,
        ConfigDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return key is null
            ? AdminResults.NotFound("API 密钥")
            : TypedResults.Ok(View(key.DebugCaptureUntil, timeProvider.GetUtcNow()));
    }

    private static async Task<IResult> SetApiKeyAsync(
        int id,
        DebugSwitchInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        IOptions<DebugCaptureOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (key is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!TryResolve(input, options.Value, now, out DateTimeOffset? until, out IResult? problem))
        {
            return problem!;
        }

        key.DebugCaptureUntil = until;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(View(until, now));
    }

    private static async Task<IResult> GetPayloadsAsync(
        Guid id,
        bool? download,
        DebugDbContext db,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        List<DebugPayload> payloads = await db.DebugPayloads.AsNoTracking()
            .Where(p => p.RequestId == id)
            .OrderBy(p => p.AttemptOrdinal)
            .ThenBy(p => p.Kind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DebugPayloadView> views =
        [
            .. payloads.Select(p => new DebugPayloadView(
                p.AttemptOrdinal,
                p.Kind,
                p.CapturedAt,
                p.HttpStatusCode,
                p.Headers,
                p.Body,
                p.Truncated,
                p.ByteCount)),
        ];

        if (download == true)
        {
            // 整个 Request 的原文打成一个 JSON 文件下载（Q162）。
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"ntm-debug-{id:N}.json\"";
        }

        return TypedResults.Ok(views);
    }

    private static bool TryResolve(
        DebugSwitchInput input,
        DebugCaptureOptions options,
        DateTimeOffset now,
        out DateTimeOffset? until,
        out IResult? problem)
    {
        until = null;
        problem = null;

        int minutes = input.Minutes ?? options.DefaultDurationMinutes;
        if (minutes == 0)
        {
            return true;
        }

        int max = options.MaxDurationHours * 60;
        if (minutes < 0 || minutes > max)
        {
            problem = new ValidationErrors()
                .Add("minutes", $"时长要在 1 到 {max} 分钟之间；0 表示关掉。")
                .ToResult();
            return false;
        }

        // 库里按 Unix 毫秒存（ADR-0012）；先取整，PUT 回的值才和之后 GET 读到的一样。
        until = DateTimeOffset.FromUnixTimeMilliseconds(now.AddMinutes(minutes).ToUnixTimeMilliseconds());
        return true;
    }

    private static DebugSwitchView View(DateTimeOffset? until, DateTimeOffset now) =>
        new(DebugSwitch.IsOn(until, now) ? until : null);

    private static DebugCaptureStatusView Status(
        DateTimeOffset? until,
        DebugCaptureSink sink,
        DebugCaptureOptions options,
        DateTimeOffset now) =>
        new(
            DebugSwitch.IsOn(until, now) ? until : null,
            sink.Dropped,
            options.DefaultDurationMinutes,
            options.MaxDurationHours * 60,
            options.RetentionHours);
}
