using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Protocol;

namespace Ntm.Gateway.Configuration;

/// <summary>
/// 持有当前的配置快照。写入后调用 <see cref="ReloadAsync"/> 整份重建，用 Interlocked 换上。
/// 读的一侧永远拿到一份自洽的快照，不加锁。
/// 重建是串行的：两次保存几乎同时触发重建时，先读库的那次如果后完成，会用旧数据盖掉新快照。
/// 排队之后，后开始的那次一定读到前一次之后提交的数据，也一定最后换上。
/// </summary>
public sealed partial class ConfigSnapshotProvider(
    IServiceScopeFactory scopeFactory,
    ChannelKeyProtector protector,
    ILogger<ConfigSnapshotProvider> logger) : IDisposable
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private ConfigSnapshot _current = ConfigSnapshot.Empty;

    public ConfigSnapshot Current => Volatile.Read(ref _current);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RebuildAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public void Dispose() => _reloadGate.Dispose();

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        List<ApiKey> tokens = await db.ApiKeys
            .Where(t => t.Enabled)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<Channel> channels = await db.Channels
            .Where(c => c.Enabled)
            .Include(c => c.Keys)
            .Include(c => c.UpstreamModels)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<PublishedModel> publishedModels = await db.PublishedModels
            .Include(m => m.Rules)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<PriceRow> priceRows = await db.PriceRows
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // 五项全空的行算不出费用，不放进快照：计费那边只需要判断「有没有」。
        var prices = new Dictionary<(string Provider, string UpstreamModel), ModelPrice>();
        foreach (PriceRow row in priceRows.Where(r => !r.IsEmpty))
        {
            prices[(row.Provider, row.UpstreamModel)] = new ModelPrice(row.ToPriceSet(), row.ToLongContext(), row.ImagePrice);
        }

        var tokenCandidates = tokens.ToDictionary(
            t => t.Fingerprint,
            t => new ApiKeySnapshot
            {
                ApiKeyId = t.Id,
                Name = t.Name,
                Fingerprint = t.Fingerprint,
                CostLimitNanoUsd = t.CostLimitNanoUsd,
                ExpiresAt = t.ExpiresAt,
                MaxConcurrency = t.MaxConcurrency,
                AllowedModels = t.AllowedModels.ToFrozenSet(StringComparer.Ordinal),
                AllowedChannelIds = t.AllowedChannelIds.ToFrozenSet(),
                BlockedChannelIds = t.BlockedChannelIds.ToFrozenSet(),
                DebugCaptureUntil = t.DebugCaptureUntil,
            },
            StringComparer.Ordinal);

        var channelCandidates = new List<ChannelSnapshot>(channels.Count);
        foreach (Channel channel in channels)
        {
            channelCandidates.Add(BuildChannel(channel, channel.Keys.Where(k => k.Enabled)));
        }

        List<PublishedModelRoute> routes = BuildRoutes(publishedModels, channelCandidates);
        foreach (PublishedModelRoute route in routes)
        {
            foreach (RuleProblem problem in route.Problems)
            {
                LogRuleProblem(logger, route.Name, problem.RuleOrdinal, problem.Message);
            }
        }

        GatewaySettings? settings = await db.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == GatewaySettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        var snapshot = new ConfigSnapshot(tokenCandidates, channelCandidates, routes, prices)
        {
            DebugCaptureUntil = settings?.DebugCaptureUntil,
        };
        Interlocked.Exchange(ref _current, snapshot);

        LogSnapshotRebuilt(logger, channelCandidates.Count, tokenCandidates.Count, snapshot.RoutesByName.Count);
    }

    /// <summary>
    /// Workbench 的渠道测试用（Q200）：直接从数据库读一个渠道，停用的渠道也读。
    /// 给了 <paramref name="channelKeyId"/> 就只带那一把密钥（停用的也行），否则带全部启用的密钥。
    /// 渠道不存在时返回 null。
    /// </summary>
    public async Task<ChannelSnapshot?> LoadChannelAsync(int channelId, int? channelKeyId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        Channel? channel = await db.Channels
            .Include(c => c.Keys)
            .Include(c => c.UpstreamModels)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        IEnumerable<ChannelKey> keys = channelKeyId is { } id
            ? channel.Keys.Where(k => k.Id == id)
            : channel.Keys.Where(k => k.Enabled);
        return BuildChannel(channel, keys);
    }

    private ChannelSnapshot BuildChannel(Channel channel, IEnumerable<ChannelKey> keySource)
    {
        var keys = new List<ChannelKeySnapshot>();
        foreach (ChannelKey key in keySource.OrderBy(k => k.Priority))
        {
            string secret;
            try
            {
                secret = protector.Unprotect(key.ProtectedSecret);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // 密钥环换了或丢了。这把密钥用不了，但其余配置照常服务，
                // 而且必须说出来——否则表现成「渠道莫名其妙没有可用密钥」。
                LogKeyUnprotectFailed(logger, ex, channel.Name, key.Name);
                continue;
            }

            keys.Add(new ChannelKeySnapshot
            {
                ChannelKeyId = key.Id,
                Name = key.Name,
                Secret = secret,
                Fingerprint = key.SecretFingerprint,
                Prefix = key.SecretPrefix,
                Priority = key.Priority,
                RequestsPerMinute = key.RequestsPerMinute,
                MaxConcurrency = key.MaxConcurrency,
                AllowedModels = key.AllowedModels.ToFrozenSet(StringComparer.Ordinal),
            });
        }

        Upstream.ProxyAddress? proxy = null;
        if (channel.ProtectedProxyUrl is not null)
        {
            proxy = ReadProxy(channel);

            // 代理解不开或不认得：整个渠道退出选路（清空密钥），绝不悄悄改成直连——
            // 配代理往往就是为了不让上游看到这台机器的地址。
            if (proxy is null)
            {
                keys.Clear();
            }
        }

        return new ChannelSnapshot
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            BaseUrl = channel.BaseUrl,
            Path = channel.Path,
            ApiFormat = channel.ApiFormat,
            AnthropicBearerAuth = channel.AnthropicBearerAuth,
            AutoCacheBreakpoints = channel.AutoCacheBreakpoints,
            KeysShareAccount = channel.KeysShareAccount,
            ThinkingStyle = channel.ThinkingStyle,
            Provider = channel.Provider,
            Priority = channel.Priority,
            Weight = channel.Weight,
            MultiplierBasisPoints = channel.MultiplierBasisPoints,
            Keys = keys,
            UpstreamModels = [.. channel.UpstreamModels.Select(m => m.Name).Order(StringComparer.Ordinal)],
            DailyCostLimitNanoUsd = channel.DailyCostLimitNanoUsd,
            TimeWindows = [.. channel.TimeWindows.Select(w => new Restrictions.TimeWindow(w.Weekdays, w.StartMinute, w.EndMinute))],
            Proxy = proxy,
            Rewriter = Rewrite.RequestRewriter.From(channel.RewriteRules),
            DebugCaptureUntil = channel.DebugCaptureUntil,
        };
    }

    private Upstream.ProxyAddress? ReadProxy(Channel channel)
    {
        string plain;
        try
        {
            plain = protector.UnprotectProxy(channel.ProtectedProxyUrl!);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            LogProxyUnprotectFailed(logger, ex, channel.Name);
            return null;
        }

        if (!Upstream.ProxyAddress.TryParse(plain, out Upstream.ProxyAddress? proxy, out string? error))
        {
            LogProxyInvalid(logger, channel.Name, error);
            return null;
        }

        return proxy;
    }

    /// <summary>
    /// 把每个下发模型的匹配规则在渠道上展开成候选，再按 Priority 分档。
    /// 正则在这里跑，热路径上只剩查字典。管理页面的规则预览也调它——预览与选路用同一份代码，
    /// 才不会出现「预览里有、请求却打不到」。
    /// </summary>
    public static List<PublishedModelRoute> BuildRoutes(
        IReadOnlyList<PublishedModel> publishedModels,
        IReadOnlyList<ChannelSnapshot> channels)
    {
        var routes = new List<PublishedModelRoute>(publishedModels.Count);
        foreach (PublishedModel model in publishedModels.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            // 没有可用密钥、这种 ApiFormat 服务不了这种请求、或者自定义路径推不出这类端点的渠道（Q224），
            // 不参与匹配——否则它会先到先得地占住落点，却永远打不出去。
            List<ChannelSnapshot> eligible = [.. channels.Where(c =>
                c.Keys.Count > 0
                && ApiFormatCapability.Supports(c.ApiFormat, model.RequestKind)
                && (model.RequestKind == RequestKind.Chat
                    || Upstream.UpstreamAddress.TryBuildRequestUri(c.BaseUrl, c.Path, c.ApiFormat, model.RequestKind, out _)))];
            Dictionary<int, ChannelSnapshot> byId = eligible.ToDictionary(c => c.ChannelId);

            MatchResult result = MatchRuleEvaluator.Evaluate(
                model.Rules,
                [.. eligible.Select(c => new MatchableChannel
                {
                    ChannelId = c.ChannelId,
                    ChannelName = c.ChannelName,
                    Priority = c.Priority,
                    Weight = c.Weight,
                    UpstreamModels = c.UpstreamModels,
                })]);

            // 档内保留匹配给出的确定顺序；那就是故障转移的顺序。
            List<PriorityGroup> groups = [.. result.Candidates
                .Select(m => new RouteCandidate
                {
                    Channel = byId[m.ChannelId],
                    UpstreamModel = m.UpstreamModel,
                    Priority = m.Priority,
                    Weight = m.Weight,
                    RuleOrdinal = m.RuleOrdinal,
                })
                .GroupBy(c => c.Priority)
                .OrderByDescending(g => g.Key)
                .Select(g => new PriorityGroup { Priority = g.Key, Candidates = [.. g] })];

            routes.Add(new PublishedModelRoute
            {
                PublishedModelId = model.Id,
                Name = model.Name,
                DisplayName = model.DisplayName,
                RequestKind = model.RequestKind,
                Enabled = model.Enabled,
                CreatedAt = model.CreatedAt,
                PriorityGroups = groups,
                Problems = result.Problems,
            });
        }

        return routes;
    }

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "下发模型 {Model} 的第 {Ordinal} 条匹配规则无效，已跳过：{Problem}")]
    private static partial void LogRuleProblem(ILogger logger, string model, int ordinal, string problem);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "渠道 {Channel} 的代理地址解不开，可能是 Data Protection 密钥环变了。这个渠道已退出选路，不会改成直连。")]
    private static partial void LogProxyUnprotectFailed(ILogger logger, Exception exception, string channel);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "渠道 {Channel} 的代理地址无效（{Problem}），这个渠道已退出选路。")]
    private static partial void LogProxyInvalid(ILogger logger, string channel, string problem);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "渠道 {Channel} 的密钥 {Key} 解不开，可能是 Data Protection 密钥环变了。该密钥已跳过。")]
    private static partial void LogKeyUnprotectFailed(ILogger logger, Exception exception, string channel, string key);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "配置快照已重建：{ChannelCount} 个渠道，{ApiKeyCount} 个 API 密钥，{ModelCount} 个已启用的下发模型。")]
    private static partial void LogSnapshotRebuilt(ILogger logger, int channelCount, int apiKeyCount, int modelCount);
}
