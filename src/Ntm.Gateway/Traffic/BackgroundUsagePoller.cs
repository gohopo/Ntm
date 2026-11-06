using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Routing;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// background 响应的用量由网关主动去拉（Q305，ADR-0020）。客户端一直不来取时，
/// 这里按 30 秒、之后每分钟、满 10 分钟后每 10 分钟、满 1 小时后每 30 分钟（Q317）去产出它的那把渠道密钥 GET 一次；
/// 到了终态交给 <see cref="ResponseUsageBackfill"/>，满 24 小时或上游 404 就放弃。
/// 和按 id 的接口一样不受限制拦、不留请求记录。状态在库里，重启后接着拉。
/// </summary>
public sealed partial class BackgroundUsagePoller(
    IServiceScopeFactory scopes,
    ConfigSnapshotProvider snapshots,
    UpstreamForwarder forwarder,
    ResponseLandingTable landings,
    ResponseUsageBackfill backfill,
    TimeProvider timeProvider,
    ILogger<BackgroundUsagePoller> logger) : BackgroundService
{
    public static readonly TimeSpan FirstPollAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FastPhase = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SlowPhase = TimeSpan.FromHours(1);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>同时最多拉这么多个。</summary>
    public const int MaxConcurrency = 4;

    /// <summary>一轮最多看这么多条，最早的先看。</summary>
    public const int BatchSize = 200;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.Ordinal) { "completed", "failed", "cancelled", "incomplete" };

    // 下次什么时候拉。只在内存里：重启后都从「现在到期」开始，最多多拉一次。
    private readonly ConcurrentDictionary<string, DateTimeOffset> _due = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // 一轮出错不能让轮询整个停掉。
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogRoundFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>拉一轮：库里还在等用量、到期了的落点各 GET 一次。返回这一轮拉了几个。</summary>
    internal async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset youngest = now - FirstPollAfter;

        List<ResponseLanding> waiting;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
            waiting = await db.ResponseLandings
                .AsNoTracking()
                .Where(l => l.AwaitingUsage && l.Kind == ResponseLandingKind.Response && l.CreatedAt <= youngest)
                .OrderBy(l => l.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        List<ResponseLanding> due = [];
        foreach (ResponseLanding stored in waiting)
        {
            if (landings.Prime(stored) is not { AwaitingUsage: true } landing)
            {
                _due.TryRemove(stored.Id, out _);
                continue;
            }

            if (now - landing.CreatedAt >= GiveUpAfter)
            {
                GiveUp(landing, "满 24 小时还没拿到终态");
                continue;
            }

            if (_due.TryGetValue(landing.Id, out DateTimeOffset next) && next > now)
            {
                continue;
            }

            due.Add(landing);
        }

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken },
            async (landing, ct) => await PollAsync(landing, now, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return due.Count;
    }

    private async Task PollAsync(ResponseLanding landing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ConfigSnapshot snapshot = snapshots.Current;
        ChannelSnapshot? channel = snapshot.Channels.FirstOrDefault(c => c.ChannelId == landing.ChannelId);
        ChannelKeySnapshot? key = channel is null ? null : ResponsesResourceRelay.KeyFor(channel, landing);
        Uri? uri = channel is null
            ? null
            : ResponsesResourceRelay.ResourceUri(channel, ResponseLandingKind.Response, landing.Id, "", default);
        if (channel is null || key is null || uri is null)
        {
            // 渠道或密钥暂时用不了（停用、改了配置）：过一会儿再看，满 24 小时就放弃。
            Reschedule(landing, now);
            return;
        }

        var request = new ForwardRequest
        {
            RequestUri = uri,
            Method = HttpMethod.Get,
            Proxy = channel.Proxy,
            Secret = key.Secret,
            UpstreamApiFormat = ApiFormat.OpenAiResponses,
            InboundApiFormat = ApiFormat.OpenAiResponses,
            Body = ReadOnlyMemory<byte>.Empty,
            Rewriter = channel.Rewriter,
        };

        (int status, byte[]? body) = await forwarder.ExchangeAsync(request, RequestTimeout, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (status == 404)
        {
            GiveUp(landing, "上游说没有这个响应（404）");
            return;
        }

        if (body is null)
        {
            Reschedule(landing, now);
            return;
        }

        (string? state, _) = ResponsesResponseInspector.ReadSettlement(body);
        if (state is null || !TerminalStatuses.Contains(state))
        {
            Reschedule(landing, now);
            return;
        }

        _due.TryRemove(landing.Id, out _);
        await backfill.TryBackfillAsync(landing, body, cancellationToken).ConfigureAwait(false);
    }

    private void Reschedule(ResponseLanding landing, DateTimeOffset now)
    {
        TimeSpan age = now - landing.CreatedAt;
        TimeSpan interval = age < FastPhase ? FastInterval : age < SlowPhase ? SlowInterval : IdleInterval;
        _due[landing.Id] = now + interval;
    }

    private void GiveUp(ResponseLanding landing, string why)
    {
        _due.TryRemove(landing.Id, out _);
        if (landings.GiveUpSettle(landing))
        {
            LogGaveUp(logger, landing.Id, landing.RequestId, why);
        }
    }

    [LoggerMessage(EventId = 2311, Level = LogLevel.Information, Message = "不再等 background 响应 {ResponseId} 的用量（请求 {RequestId}）：{Why}。")]
    private static partial void LogGaveUp(ILogger logger, string responseId, Guid? requestId, string why);

    [LoggerMessage(EventId = 2312, Level = LogLevel.Warning, Message = "拉 background 响应用量的这一轮出错了。")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
