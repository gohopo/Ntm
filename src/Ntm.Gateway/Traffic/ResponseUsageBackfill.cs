using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// background 响应的用量回填（Q277）：创建时上游只回了 queued，没有用量；之后按 id 取回看到终态
/// 并带着 usage 时，把用量和费用补进原来那条 Request 和它的 Attempt，汇总表和费用上限的计数一起补。
/// 只补一次（落点的 AwaitingUsage 清掉），请求的状态不变。
/// </summary>
public sealed partial class ResponseUsageBackfill(
    IServiceScopeFactory scopes,
    ConfigSnapshotProvider snapshots,
    CostLedger ledger,
    ResponseLandingTable landings,
    ILogger<ResponseUsageBackfill> logger)
{
    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.Ordinal) { "completed", "failed", "cancelled", "incomplete" };

    /// <summary>看一眼取回的响应体，该回填就回填。回填失败只记日志，不影响把响应交给客户端。</summary>
    public async Task TryBackfillAsync(ResponseLanding landing, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(landing);
        if (!landing.AwaitingUsage || landing.RequestId is not { } requestId)
        {
            return;
        }

        (string? status, Usage usage) = ResponsesResponseInspector.ReadSettlement(body.Span);
        if (status is null || !TerminalStatuses.Contains(status))
        {
            return;
        }

        if (!landings.TryBeginSettle(landing.Id))
        {
            return;
        }

        bool settled = false;
        try
        {
            settled = await ApplyAsync(landing, requestId, usage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 回填是顺手的事：失败不能让取回本身失败。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, landing.Id);
        }
        finally
        {
            landings.EndSettle(landing, settled);
        }
    }

    /// <returns>以后还要不要再试：请求记录还没落盘时返回 false，下次取回再试。</returns>
    private async Task<bool> ApplyAsync(ResponseLanding landing, Guid requestId, Usage usage, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        RequestRecord? request = await db.Requests
            .Include(r => r.Attempts)
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            // 记录还在写入队列里（创建后马上就查完了）：下次取回再补。
            return false;
        }

        AttemptRecord? attempt = request.Attempts.FirstOrDefault(a => a.ResponseId == landing.Id)
            ?? request.Attempts.OrderBy(a => a.Ordinal).LastOrDefault(a => a.FailureClass == FailureClass.None);

        // 上游没给用量（失败、取消的常常没有），或者这次已经有用量了：没什么可补，以后也不再试。
        if (attempt is null || !usage.HasValue || attempt.UsageSource != UsageSource.Unavailable)
        {
            return true;
        }

        bool wasUnpriced = request.CostNanoUsd is null && (TotalTokens(request) > 0 || request.ImageCount > 0);

        attempt.InputTokens = usage.InputTokens;
        attempt.CacheReadTokens = usage.CacheReadTokens;
        attempt.CacheWrite5mTokens = usage.CacheWrite5mTokens;
        attempt.CacheWrite1hTokens = usage.CacheWrite1hTokens;
        attempt.OutputTokens = usage.OutputTokens;
        attempt.UsageSource = usage.Source;

        ConfigSnapshot snapshot = snapshots.Current;
        ChannelSnapshot? channel = snapshot.Channels.FirstOrDefault(c => c.ChannelId == attempt.ChannelId);
        CostCalculator.Apply(attempt, snapshot.FindPrice(channel?.Provider, attempt.UpstreamModel));

        request.InputTokens += attempt.InputTokens;
        request.CacheReadTokens += attempt.CacheReadTokens;
        request.CacheWrite5mTokens += attempt.CacheWrite5mTokens;
        request.CacheWrite1hTokens += attempt.CacheWrite1hTokens;
        request.OutputTokens += attempt.OutputTokens;
        if (attempt.CostNanoUsd is { } cost)
        {
            request.CostNanoUsd = (request.CostNanoUsd ?? 0) + cost;
        }

        if (attempt.UsageSource > request.UsageSource)
        {
            request.UsageSource = attempt.UsageSource;
        }

        bool nowUnpriced = request.CostNanoUsd is null && TotalTokens(request) > 0;
        await TrafficWriter.AddBackfilledUsageAsync(db, request, attempt, nowUnpriced && !wasUnpriced, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        ledger.RecordBackfill(attempt.ChannelId, request.ApiKeyId, attempt.StartedAt, attempt.CostNanoUsd ?? 0);
        LogBackfilled(logger, landing.Id, requestId, attempt.InputTokens + attempt.CacheReadTokens, attempt.OutputTokens);
        return true;
    }

    private static long TotalTokens(RequestRecord request) =>
        request.InputTokens
        + request.CacheReadTokens
        + request.CacheWrite5mTokens
        + request.CacheWrite1hTokens
        + request.OutputTokens;

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "回填了 background 响应 {ResponseId} 的用量（请求 {RequestId}，输入 {Input}，输出 {Output}）。")]
    private static partial void LogBackfilled(ILogger logger, string responseId, Guid requestId, long input, long output);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "回填 background 响应 {ResponseId} 的用量失败。")]
    private static partial void LogFailed(ILogger logger, Exception exception, string responseId);
}
