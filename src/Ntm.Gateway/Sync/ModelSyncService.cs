using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Upstream;

namespace Ntm.Gateway.Sync;

/// <summary>一次 ModelSync 的结果，也是最近一次结果在渠道上记下的内容（Q140）。</summary>
public sealed record ModelSyncOutcome(bool Succeeded, string Message, int Added, int Removed, DateTimeOffset At);

/// <summary>
/// ModelSync（Q39、Q135–Q142）：从上游取回一个渠道的模型清单，全量替换同步来的条目，
/// 人工录入的条目原样保留。它从不创建、也从不启用 PublishedModel。
///
/// 失败只记在渠道上，不进 Cooldown、不影响 Health（Q136）：列模型接口坏了不代表对话接口坏了。
/// </summary>
public sealed partial class ModelSyncService(
    IServiceScopeFactory scopeFactory,
    ConfigSnapshotProvider snapshots,
    ChannelKeyProtector protector,
    CooldownRegistry cooldowns,
    ModelListFetcher fetcher,
    TimeProvider timeProvider,
    ILogger<ModelSyncService> logger)
{
    /// <summary>一次同步最多换几把密钥（Q136）。</summary>
    public const int MaxKeysPerRun = 3;

    /// <summary>事件保留天数（Q140）。</summary>
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    /// <summary>
    /// 同步一个渠道。同一个渠道同时只跑一次：后台任务和「立即同步」撞上时后来的排队。
    /// 渠道不存在时返回 null。
    /// </summary>
    public async Task<ModelSyncOutcome?> SyncAsync(int channelId, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelSyncOutcome?> SyncCoreAsync(int channelId, CancellationToken cancellationToken)
    {
        Channel? channel;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            channel = await db.Channels
                .Include(c => c.Keys)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
        }

        if (channel is null)
        {
            return null;
        }

        // 先在库外把清单取回来（可能要几十秒），再开一次短事务写入。
        (IReadOnlyList<string>? models, string error) = await FetchAsync(channel, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (models is null)
        {
            LogSyncFailed(logger, channel.Name, error);
            await RecordAsync(channelId, new ModelSyncOutcome(false, error, 0, 0, now), cancellationToken).ConfigureAwait(false);
            return new ModelSyncOutcome(false, error, 0, 0, now);
        }

        return await ApplyAsync(channelId, models, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<string>? Models, string Error)> FetchAsync(Channel channel, CancellationToken cancellationToken)
    {
        ProxyAddress? proxy = null;
        if (channel.ProtectedProxyUrl is not null)
        {
            string plain;
            try
            {
                plain = protector.UnprotectProxy(channel.ProtectedProxyUrl);
            }
            catch (CryptographicException)
            {
                return (null, "存着的代理地址解不开（密钥环换过？），不会改成直连去取清单。");
            }

            if (!ProxyAddress.TryParse(plain, out proxy, out string? proxyError))
            {
                return (null, "代理地址无效：" + proxyError);
            }
        }

        List<ChannelKey> usable = [.. channel.Keys
            .Where(k => k.Enabled)
            .OrderByDescending(k => k.Priority)
            .ThenBy(k => k.Id)];
        if (usable.Count == 0)
        {
            return (null, "这个渠道没有启用的渠道密钥。");
        }

        List<ChannelKey> candidates = [.. usable.Where(k => !cooldowns.IsKeyCooling(k.SecretFingerprint))];
        if (candidates.Count == 0)
        {
            return (null, "所有启用的渠道密钥都在冷却，等冷却结束再同步。");
        }

        var target = new ModelListTarget(channel.BaseUrl, channel.Path, channel.ApiFormat, proxy, channel.AnthropicBearerAuth);
        List<string> errors = [];
        foreach (ChannelKey key in candidates.Take(MaxKeysPerRun))
        {
            string secret;
            try
            {
                secret = protector.Unprotect(key.ProtectedSecret);
            }
            catch (CryptographicException)
            {
                errors.Add($"{key.SecretPrefix}…：密钥解不开（密钥环换过？）");
                continue;
            }

            ModelListResult result = await fetcher.FetchAsync(target, secret, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return (result.Models, "");
            }

            errors.Add($"{key.SecretPrefix}…：{result.Error}");
        }

        return (null, string.Join("；", errors));
    }

    private async Task<ModelSyncOutcome?> ApplyAsync(
        int channelId,
        IReadOnlyList<string> models,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        Channel? channel = await db.Channels
            .Include(c => c.UpstreamModels)
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        var fetched = new HashSet<string>(models, StringComparer.Ordinal);
        var existing = channel.UpstreamModels.ToDictionary(m => m.Name, StringComparer.Ordinal);

        List<string> added = [];
        List<string> removed = [];
        foreach (UpstreamModel model in channel.UpstreamModels.ToList())
        {
            if (fetched.Contains(model.Name))
            {
                // 人工录入的条目上游也列了：仍算人工录入，只记一下最近见过（Q138）。
                model.LastSeenAt = now;
            }
            else if (model.Source == UpstreamModelSource.Synced)
            {
                channel.UpstreamModels.Remove(model);
                removed.Add(model.Name);
            }
        }

        foreach (string name in models.Where(n => !existing.ContainsKey(n)))
        {
            channel.UpstreamModels.Add(new UpstreamModel
            {
                Name = name,
                Source = UpstreamModelSource.Synced,
                CreatedAt = now,
                LastSeenAt = now,
            });
            added.Add(name);
        }

        if (channel.UpstreamModels.Count > ModelListFetcher.MaxModels)
        {
            string tooMany = $"同步后这个渠道会有 {channel.UpstreamModels.Count} 个上游模型，超过 {ModelListFetcher.MaxModels} 个的上限。";
            db.ChangeTracker.Clear();
            await RecordAsync(channelId, new ModelSyncOutcome(false, tooMany, 0, 0, now), cancellationToken).ConfigureAwait(false);
            return new ModelSyncOutcome(false, tooMany, 0, 0, now);
        }

        string message = added.Count == 0 && removed.Count == 0
            ? $"上游列出 {models.Count} 个模型，清单没有变化。"
            : $"上游列出 {models.Count} 个模型，新增 {added.Count} 个，移除 {removed.Count} 个。";
        var outcome = new ModelSyncOutcome(true, message, added.Count, removed.Count, now);
        Write(channel, outcome);

        bool changed = added.Count > 0 || removed.Count > 0;
        Dictionary<string, List<string>> routedBefore = changed ? Routed(channelId) : [];

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 同步期间渠道被删了。
            return null;
        }

        if (!changed)
        {
            return outcome;
        }

        // 清单真的变了才重建快照，一次同步最多一次（Q142）。
        await snapshots.ReloadAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, List<string>> routedAfter = Routed(channelId);

        List<ModelSyncEvent> events = [];
        foreach (string name in added)
        {
            if (routedAfter.TryGetValue(name, out List<string>? published))
            {
                events.Add(Event(ModelSyncEventKind.EnteredRouting, channel, name, published, now));
            }
        }

        foreach (string name in removed)
        {
            if (routedBefore.TryGetValue(name, out List<string>? published))
            {
                events.Add(Event(ModelSyncEventKind.LeftRouting, channel, name, published, now));
            }
        }

        DateTimeOffset cutoff = now - EventRetention;
        await db.ModelSyncEvents.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (events.Count > 0)
        {
            db.ModelSyncEvents.AddRange(events);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            int entered = events.Count(e => e.Kind == ModelSyncEventKind.EnteredRouting);
            LogRoutingChanged(logger, channel.Name, entered, events.Count - entered);
        }

        return outcome;
    }

    /// <summary>只写结果字段。不动 UpdatedAt：那是人改配置的时间。</summary>
    private async Task RecordAsync(int channelId, ModelSyncOutcome outcome, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        string message = Truncate(outcome.Message);
        await db.Channels
            .Where(c => c.Id == channelId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.LastModelSyncAt, outcome.At)
                    .SetProperty(c => c.LastModelSyncSucceeded, outcome.Succeeded)
                    .SetProperty(c => c.LastModelSyncMessage, message)
                    .SetProperty(c => c.LastModelSyncAdded, outcome.Added)
                    .SetProperty(c => c.LastModelSyncRemoved, outcome.Removed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Write(Channel channel, ModelSyncOutcome outcome)
    {
        channel.LastModelSyncAt = outcome.At;
        channel.LastModelSyncSucceeded = outcome.Succeeded;
        channel.LastModelSyncMessage = Truncate(outcome.Message);
        channel.LastModelSyncAdded = outcome.Added;
        channel.LastModelSyncRemoved = outcome.Removed;
    }

    /// <summary>这个渠道上哪些上游模型现在在选路里，以及各被哪些启用的 PublishedModel 用着。</summary>
    private Dictionary<string, List<string>> Routed(int channelId)
    {
        var routed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (PublishedModelRoute route in snapshots.Current.Routes.Where(r => r.Enabled))
        {
            foreach (RouteCandidate candidate in route.PriorityGroups.SelectMany(g => g.Candidates).Where(c => c.ChannelId == channelId))
            {
                if (!routed.TryGetValue(candidate.UpstreamModel, out List<string>? published))
                {
                    routed[candidate.UpstreamModel] = published = [];
                }

                if (!published.Contains(route.Name))
                {
                    published.Add(route.Name);
                }
            }
        }

        return routed;
    }

    private static ModelSyncEvent Event(ModelSyncEventKind kind, Channel channel, string upstreamModel, List<string> published, DateTimeOffset now)
    {
        string joined = string.Join(", ", published.Order(StringComparer.Ordinal));
        if (joined.Length > ModelSyncEvent.MaxPublishedModelsLength)
        {
            joined = joined[..(ModelSyncEvent.MaxPublishedModelsLength - 1)] + "…";
        }

        return new ModelSyncEvent
        {
            OccurredAt = now,
            Kind = kind,
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            UpstreamModel = upstreamModel,
            PublishedModels = joined,
        };
    }

    private static string Truncate(string message) => message.Length > 1000 ? message[..999] + "…" : message;

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "渠道 {Channel} 的模型同步失败：{Error}")]
    private static partial void LogSyncFailed(ILogger logger, string channel, string error);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "渠道 {Channel} 的模型同步改变了选路：{Entered} 个上游模型进入，{Left} 个离开")]
    private static partial void LogRoutingChanged(ILogger logger, string channel, int entered, int left);
}
