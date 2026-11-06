using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Traffic;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;

namespace Ntm.Server.Admin;

/// <summary>
/// 新建和整体修改渠道时提交的内容。PUT 时没给的可选项保持原值；upstreamModels 为 null 表示不动。
/// proxyUrl：null 不动，空字符串去掉代理，其余是新的代理地址（Q131：原文只写不读）。
/// </summary>
internal sealed record ChannelInput(
    string? Name,
    string? BaseUrl,
    string? Path,
    ApiFormat? ApiFormat,
    string? Provider,
    int? Priority,
    int? Weight,
    decimal? Multiplier,
    bool? Enabled,
    IReadOnlyList<string?>? UpstreamModels,
    string? ProxyUrl = null,
    bool? ModelSyncEnabled = null,
    bool? AnthropicBearerAuth = null,
    bool? AutoCacheBreakpoints = null,
    AnthropicThinkingStyle? ThinkingStyle = null,
    bool? KeysShareAccount = null);

internal sealed record KeyInput(string? Secret, string? Name, int? Priority);

internal sealed record KeyPatch(string? Name, int? Priority, bool? Enabled);

internal sealed record ChannelView(
    int Id,
    string Name,
    bool Enabled,
    string BaseUrl,
    string? Path,
    string? RequestUrl,
    ApiFormat ApiFormat,
    string Provider,
    int Priority,
    int Weight,
    decimal Multiplier,
    int KeyCount,
    int EnabledKeyCount,
    int UpstreamModelCount,
    int CoolingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ProxyView? Proxy = null,
    ModelSyncView? ModelSync = null,
    bool AnthropicBearerAuth = false,
    bool AutoCacheBreakpoints = true,
    double? LowestHealth = null,
    AnthropicThinkingStyle ThinkingStyle = AnthropicThinkingStyle.Auto,
    bool KeysShareAccount = false);

/// <summary>代理只给打码后的样子（Q131）。<see cref="Readable"/> 为 false 时这个渠道整个退出了选路。</summary>
internal sealed record ProxyView(string? Masked, bool Readable);

/// <summary>ModelSync 的开关和最近一次结果（Q140）。从没同步过时 <see cref="LastAt"/> 为 null。</summary>
internal sealed record ModelSyncView(
    bool Enabled,
    DateTimeOffset? LastAt,
    bool? LastSucceeded,
    string? LastMessage,
    int LastAdded,
    int LastRemoved);

/// <summary>密钥原文只写不读；<see cref="Readable"/> 为 false 说明当前密钥环解不开它，选路时会被跳过。</summary>
internal sealed record ChannelKeyView(
    int Id,
    string Name,
    string Prefix,
    bool Enabled,
    int Priority,
    bool Readable,
    DateTimeOffset CreatedAt,
    KeyRestrictionsView Restrictions);

internal sealed record UpstreamModelView(
    string Name,
    UpstreamModelSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

internal sealed record CooldownView(
    CooldownScope Scope,
    string? KeyPrefix,
    string? UpstreamModel,
    DateTimeOffset Until,
    FailureClass Failure,
    int? KeyId = null);

/// <summary>解除了几条冷却（Q262）。</summary>
internal sealed record CooldownClearView(int Cleared);

/// <summary>只作用在这个渠道上的匹配规则。删渠道会连它们一起删掉，界面要先提醒。</summary>
internal sealed record ScopedRuleView(int PublishedModelId, string PublishedModel, int Ordinal);

internal sealed record ChannelDetailView(
    ChannelView Channel,
    IReadOnlyList<ChannelKeyView> Keys,
    IReadOnlyList<UpstreamModelView> UpstreamModels,
    IReadOnlyList<CooldownView> Cooldowns,
    IReadOnlyList<ScopedRuleView> ScopedRules,
    ChannelRestrictionsView Restrictions,
    IReadOnlyList<RewriteRuleView> RewriteRules);

/// <summary>/api/channels：渠道、它的密钥和上游模型清单。</summary>
internal static class ChannelEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxUrlLength = 500;
    private const int MaxSecretLength = 2000;
    private const int MaxModelNameLength = 300;
    private const int MaxUpstreamModels = 2000;
    private const decimal MaxMultiplier = 100m;

    private const int MaxProviderLength = 100;

    private static readonly Regex ProviderId = new(@"^[a-z0-9._-]+$", RegexOptions.CultureInvariant);
    private const string BearerPrefix = "Bearer ";

    public static RouteGroupBuilder MapChannelEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPatch("/{id:int}", SetEnabledAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        group.MapPost("/{id:int}/keys", AddKeyAsync);
        group.MapPatch("/{id:int}/keys/{keyId:int}", UpdateKeyAsync);
        group.MapDelete("/{id:int}/keys/{keyId:int}", DeleteKeyAsync);
        group.MapPost("/{id:int}/sync", SyncAsync);
        group.MapDelete("/{id:int}/cooldowns", ClearCooldownsAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        ConfigDbContext db,
        ChannelKeyProtector protector,
        CooldownRegistry cooldowns,
        HealthTracker health,
        CancellationToken cancellationToken)
    {
        List<Channel> channels = await db.Channels
            .AsNoTracking()
            .Include(c => c.Keys)
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 上游模型可能上千个，只要个数。
        Dictionary<int, int> modelCounts = await db.UpstreamModels
            .AsNoTracking()
            .GroupBy(m => m.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(CooldownKey Key, DateTimeOffset Until, FailureClass Failure)> active = cooldowns.Active();
        return TypedResults.Ok(channels
            .Select(c => ToView(c, modelCounts.GetValueOrDefault(c.Id), active, protector) with
            {
                LowestHealth = health.Samples(c.Id) is { Count: > 0 } samples ? samples.Min(x => x.Factor) : null,
            })
            .ToList());
    }

    private static async Task<IResult> GetAsync(
        int id,
        ConfigDbContext db,
        ChannelKeyProtector protector,
        CooldownRegistry cooldowns,
        CancellationToken cancellationToken)
    {
        Channel? channel = await LoadAsync(db, id, tracking: false, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        return TypedResults.Ok(await ToDetailAsync(db, channel, protector, cooldowns, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateAsync(
        ChannelInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        ChannelKeyProtector protector,
        CooldownRegistry cooldowns,
        PriceCatalogRefresher prices,
        PriceCatalogService catalog,
        PriceRowService priceRows,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        (ChannelChanges? changes, ValidationErrors errors) =
            await ValidateAsync(db, input, existing: null, cancellationToken).ConfigureAwait(false);
        if (changes is null)
        {
            return errors.ToResult();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var channel = new Channel { Name = changes.Name, BaseUrl = changes.BaseUrl, CreatedAt = now };
        Apply(channel, changes, protector, now);
        db.Channels.Add(channel);
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的渠道了。");
        }

        // 先补单价行再重建快照：快照里带着单价，顺序反了这次保存就看不到新行（Q48）。
        // 用 None 是因为管理员自己断开不该让快照停在旧的上。
        await priceRows.SyncAsync(catalog.Current, CancellationToken.None).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);

        // 新渠道可能声明了目录里还没取过的供应商，叫醒刷新循环去补（Q72）。
        prices.Wake();
        ChannelDetailView detail = await ToDetailAsync(db, channel, protector, cooldowns, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(AdminResults.Location("/api/channels", channel.Id), detail);
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        ChannelInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        ChannelKeyProtector protector,
        CooldownRegistry cooldowns,
        PriceCatalogRefresher prices,
        PriceCatalogService catalog,
        PriceRowService priceRows,
        ModelSyncScheduler syncScheduler,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await LoadAsync(db, id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        (ChannelChanges? changes, ValidationErrors errors) =
            await ValidateAsync(db, input, channel, cancellationToken).ConfigureAwait(false);
        if (changes is null)
        {
            return errors.ToResult();
        }

        bool syncTurnedOn = changes.ModelSyncEnabled && !channel.ModelSyncEnabled;
        Apply(channel, changes, protector, timeProvider.GetUtcNow());
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的渠道了。");
        }

        await priceRows.SyncAsync(catalog.Current, CancellationToken.None).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        prices.Wake();
        if (syncTurnedOn)
        {
            syncScheduler.Enqueue(channel.Id);
        }

        return TypedResults.Ok(await ToDetailAsync(db, channel, protector, cooldowns, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SetEnabledAsync(
        int id,
        EnabledInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (input.Enabled is not bool enabled)
        {
            return AdminResults.MissingEnabled();
        }

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        channel.Enabled = enabled;
        channel.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// 「立即同步」（Q135）：同步跑完再返回，结果同时记在渠道上。渠道的同步开关关着也能点，
    /// 它顺带检验渠道密钥和代理通不通。
    /// </summary>
    private static async Task<IResult> SyncAsync(
        int id,
        ModelSyncService sync,
        ConfigDbContext db,
        CancellationToken cancellationToken)
    {
        ModelSyncOutcome? outcome = await sync.SyncAsync(id, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            return AdminResults.NotFound("渠道");
        }

        bool enabled = await db.Channels
            .Where(c => c.Id == id)
            .Select(c => c.ModelSyncEnabled)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ModelSyncView(enabled, outcome.At, outcome.Succeeded, outcome.Message, outcome.Added, outcome.Removed));
    }

    private static async Task<IResult> DeleteAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        RestrictionChecker restrictions,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        // 「排除这些渠道」存的是 id 列表，没有外键兜着；不清掉的话规则里会留着一个不存在的渠道，
        // 下次编辑这条规则时校验就过不去。规则不多，整表读进来改。
        List<MatchRule> rules = await db.MatchRules.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (MatchRule rule in rules)
        {
            rule.ExcludedChannelIds.RemoveAll(excluded => excluded == id);
        }

        // API 密钥的渠道黑白名单不清：渠道 id 自增不复用，失效的 id 无害；白名单里只剩它时
        // 留着它正好让那个密钥继续什么渠道都走不了，而不是清空后变成「不限」。
        // 密钥、上游模型和只作用在这个渠道上的规则由外键级联删除。
        db.Channels.Remove(channel);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        restrictions.Costs.ForgetChannel(id);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> AddKeyAsync(
        int id,
        KeyInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        ChannelKeyProtector protector,
        ModelSyncScheduler syncScheduler,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        var errors = new ValidationErrors();
        string secret = NormalizeSecret(input.Secret);
        string fingerprint = "";
        if (secret.Length == 0)
        {
            errors.Add("secret", "请填写密钥。");
        }
        else if (secret.Length > MaxSecretLength)
        {
            errors.Add("secret", "密钥太长了。");
        }
        else if (secret.Any(char.IsWhiteSpace))
        {
            // 中间带空白的密钥拼进请求头会变成一个坏的 Authorization，十有八九是粘贴时多带了东西。
            errors.Add("secret", "密钥里不能有空格或换行。");
        }
        else
        {
            fingerprint = SecretFingerprint.Compute(secret);
            if (channel.Keys.Any(k => k.SecretFingerprint == fingerprint))
            {
                // 同一把密钥挂到别的渠道上是允许的（Q21：冷却和限速按密钥值共用），挂两次到同一个渠道没有意义。
                errors.Add("secret", "这个密钥已经在这个渠道里了。");
            }
        }

        string name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            name = DefaultKeyName(channel);
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add("name", "密钥名最多 200 个字符。");
        }
        else if (channel.Keys.Any(k => k.Name == name))
        {
            errors.Add("name", "这个渠道里已经有同名的密钥了。");
        }

        int priority = input.Priority ?? 0;
        if (priority is < AdminResults.MinPriority or > AdminResults.MaxPriority)
        {
            errors.Add("priority", "优先级要在 -1000 到 1000 之间。");
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var key = new ChannelKey
        {
            Name = name,
            ProtectedSecret = protector.Protect(secret),
            SecretFingerprint = fingerprint,
            SecretPrefix = SecretFingerprint.Prefix(secret),
            Priority = priority,
            CreatedAt = now,
        };
        channel.Keys.Add(key);
        channel.UpdatedAt = now;
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("这个渠道里已经有同名的密钥了。");
        }

        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);

        // 新建渠道时还没有渠道密钥，取不了清单；等第一把密钥加上再同步（Q141）。
        // 还没成功同步过才排，免得每加一把密钥都打一次上游。
        if (channel.ModelSyncEnabled && channel.LastModelSyncSucceeded != true)
        {
            syncScheduler.Enqueue(channel.Id);
        }

        return TypedResults.Created(
            AdminResults.Location("/api/channels", channel.Id) + "/keys/" + key.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ToKeyView(key, readable: true));
    }

    private static async Task<IResult> UpdateKeyAsync(
        int id,
        int keyId,
        KeyPatch input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        ChannelKeyProtector protector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        ChannelKey? key = channel?.Keys.FirstOrDefault(k => k.Id == keyId);
        if (channel is null || key is null)
        {
            return AdminResults.NotFound("密钥");
        }

        var errors = new ValidationErrors();
        if (input.Name is not null)
        {
            string name = input.Name.Trim();
            if (name.Length is 0 or > MaxNameLength)
            {
                errors.Add("name", "密钥名要有 1 到 200 个字符。");
            }
            else if (channel.Keys.Any(k => k.Id != keyId && k.Name == name))
            {
                errors.Add("name", "这个渠道里已经有同名的密钥了。");
            }
            else
            {
                key.Name = name;
            }
        }

        if (input.Priority is int priority)
        {
            if (priority is < AdminResults.MinPriority or > AdminResults.MaxPriority)
            {
                errors.Add("priority", "优先级要在 -1000 到 1000 之间。");
            }
            else
            {
                key.Priority = priority;
            }
        }

        if (input.Enabled is bool enabled)
        {
            key.Enabled = enabled;
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        channel.UpdatedAt = timeProvider.GetUtcNow();
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("这个渠道里已经有同名的密钥了。");
        }

        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(ToKeyView(key, IsReadable(protector, key)));
    }

    private static async Task<IResult> DeleteKeyAsync(
        int id,
        int keyId,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        ChannelKey? key = channel?.Keys.FirstOrDefault(k => k.Id == keyId);
        if (channel is null || key is null)
        {
            return AdminResults.NotFound("密钥");
        }

        db.ChannelKeys.Remove(key);
        channel.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Task<Channel?> LoadAsync(ConfigDbContext db, int id, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<Channel> query = db.Channels
            .Include(c => c.Keys)
            .Include(c => c.UpstreamModels)
            .AsSplitQuery();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    private static ChannelView ToView(
        Channel channel,
        int upstreamModelCount,
        IReadOnlyList<(CooldownKey Key, DateTimeOffset Until, FailureClass Failure)> active,
        ChannelKeyProtector protector)
    {
        HashSet<string> fingerprints = [.. channel.Keys.Select(k => k.SecretFingerprint)];
        return new ChannelView(
            channel.Id,
            channel.Name,
            channel.Enabled,
            channel.BaseUrl,
            channel.Path,
            RequestUrl(channel),
            channel.ApiFormat,
            channel.Provider,
            channel.Priority,
            channel.Weight,
            channel.MultiplierBasisPoints / 10_000m,
            channel.Keys.Count,
            channel.Keys.Count(k => k.Enabled),
            upstreamModelCount,
            active.Count(entry => Concerns(entry.Key, channel.Id, fingerprints)),
            channel.CreatedAt,
            channel.UpdatedAt,
            ReadProxy(protector, channel),
            new ModelSyncView(
                channel.ModelSyncEnabled,
                channel.LastModelSyncAt,
                channel.LastModelSyncSucceeded,
                channel.LastModelSyncMessage,
                channel.LastModelSyncAdded,
                channel.LastModelSyncRemoved),
            channel.AnthropicBearerAuth,
            channel.AutoCacheBreakpoints,
            ThinkingStyle: channel.ThinkingStyle,
            KeysShareAccount: channel.KeysShareAccount);
    }

    private static ProxyView? ReadProxy(ChannelKeyProtector protector, Channel channel)
    {
        if (channel.ProtectedProxyUrl is null)
        {
            return null;
        }

        try
        {
            string plain = protector.UnprotectProxy(channel.ProtectedProxyUrl);
            return ProxyAddress.TryParse(plain, out ProxyAddress? proxy, out _)
                ? new ProxyView(proxy.Masked, Readable: true)
                : new ProxyView(null, Readable: false);
        }
        catch (CryptographicException)
        {
            return new ProxyView(null, Readable: false);
        }
    }

    private static async Task<ChannelDetailView> ToDetailAsync(
        ConfigDbContext db,
        Channel channel,
        ChannelKeyProtector protector,
        CooldownRegistry cooldowns,
        CancellationToken cancellationToken)
    {
        int channelId = channel.Id;
        List<ScopedRuleView> scopedRules = await db.MatchRules
            .AsNoTracking()
            .Where(r => r.ChannelId == channelId)
            .OrderBy(r => r.PublishedModelId)
            .ThenBy(r => r.Ordinal)
            .Select(r => new ScopedRuleView(r.PublishedModelId, r.PublishedModel!.Name, r.Ordinal))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(CooldownKey Key, DateTimeOffset Until, FailureClass Failure)> active = cooldowns.Active();
        HashSet<string> fingerprints = [.. channel.Keys.Select(k => k.SecretFingerprint)];
        Dictionary<string, string> prefixes = channel.Keys
            .GroupBy(k => k.SecretFingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SecretPrefix, StringComparer.Ordinal);
        Dictionary<string, int> keyIds = channel.Keys
            .GroupBy(k => k.SecretFingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(k => k.Id), StringComparer.Ordinal);

        return new ChannelDetailView(
            ToView(channel, channel.UpstreamModels.Count, active, protector),
            [.. channel.Keys
                .OrderByDescending(k => k.Priority)
                .ThenBy(k => k.Id)
                .Select(k => ToKeyView(k, IsReadable(protector, k)))],
            [.. channel.UpstreamModels
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .Select(m => new UpstreamModelView(m.Name, m.Source, m.CreatedAt, m.LastSeenAt))],
            [.. active
                .Where(entry => Concerns(entry.Key, channelId, fingerprints))
                .OrderBy(entry => entry.Until)
                .Select(entry => new CooldownView(
                    entry.Key.Scope,
                    entry.Key.KeyFingerprint is { } fingerprint && prefixes.TryGetValue(fingerprint, out string? prefix) ? prefix : null,
                    entry.Key.UpstreamModel,
                    entry.Until,
                    entry.Failure,
                    entry.Key.KeyFingerprint is { } keyFingerprint && keyIds.TryGetValue(keyFingerprint, out int keyId) ? keyId : null))],
            scopedRules,
            RestrictionEndpoints.ToView(channel),
            RewriteRuleEndpoints.ToView(channel));
    }

    private static ChannelKeyView ToKeyView(ChannelKey key, bool readable) =>
        new(key.Id, key.Name, key.SecretPrefix, key.Enabled, key.Priority, readable, key.CreatedAt, RestrictionEndpoints.ToView(key));

    private static string? RequestUrl(Channel channel) =>
        UpstreamAddress.TryBuildRequestUri(channel.BaseUrl, channel.Path, channel.ApiFormat, RequestKind.Chat, out Uri? uri)
            ? uri?.AbsoluteUri
            : null;

    /// <summary>按密钥值记的冷却按指纹认（同一把密钥可以挂在几个渠道上），其余按渠道 id 认。</summary>
    /// <summary>
    /// 手动解除这个渠道的冷却（Q262）。不带参数时解除全部；带了就只解除匹配的那些。
    /// 密钥维度的冷却按密钥值记（Q21），同一份密钥挂在别的渠道上时那边也一起解除。不碰健康度。
    /// </summary>
    private static async Task<IResult> ClearCooldownsAsync(
        int id,
        string? scope,
        int? keyId,
        string? upstreamModel,
        ConfigDbContext db,
        CooldownRegistry cooldowns,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        CooldownScope? wanted = null;
        if (!string.IsNullOrEmpty(scope))
        {
            if (!Enum.TryParse(scope, ignoreCase: true, out CooldownScope parsed)
                || parsed == CooldownScope.None
                || !Enum.IsDefined(parsed)
                || int.TryParse(scope, out _))
            {
                return new ValidationErrors().Add("scope", "没有这种冷却范围。").ToResult();
            }

            wanted = parsed;
        }

        Channel? channel = await db.Channels
            .AsNoTracking()
            .Include(c => c.Keys)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        string? fingerprint = null;
        if (keyId is not null)
        {
            fingerprint = channel.Keys.FirstOrDefault(k => k.Id == keyId)?.SecretFingerprint;
            if (fingerprint is null)
            {
                return AdminResults.NotFound("渠道密钥");
            }
        }

        HashSet<string> fingerprints = [.. channel.Keys.Select(k => k.SecretFingerprint)];
        int cleared = 0;
        foreach ((CooldownKey key, _, _) in cooldowns.Active())
        {
            if (Concerns(key, id, fingerprints)
                && (wanted is null || key.Scope == wanted)
                && (fingerprint is null || key.KeyFingerprint == fingerprint)
                && (upstreamModel is null || key.UpstreamModel == upstreamModel)
                && cooldowns.Remove(key))
            {
                cleared++;
            }
        }

        if (cleared > 0)
        {
            loggers.CreateLogger("Ntm.Server.Admin.Cooldowns")
                .LogCooldownsCleared(cleared, channel.Name, channel.Id);
        }

        return TypedResults.Ok(new CooldownClearView(cleared));
    }

    private static bool Concerns(CooldownKey key, int channelId, HashSet<string> fingerprints) =>
        key.Scope is CooldownScope.Key or CooldownScope.KeyModel
            ? key.KeyFingerprint is not null && fingerprints.Contains(key.KeyFingerprint)
            : key.ChannelId == channelId;

    /// <summary>密钥环换过之后旧密钥解不开；界面要能看出来，否则症状只是「渠道没有可用密钥」。</summary>
    private static bool IsReadable(ChannelKeyProtector protector, ChannelKey key)
    {
        try
        {
            _ = protector.Unprotect(key.ProtectedSecret);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>去掉首尾空白，以及粘贴时常带上的 <c>Bearer </c>。</summary>
    private static string NormalizeSecret(string? raw)
    {
        string secret = raw?.Trim() ?? "";
        if (secret.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            secret = secret[BearerPrefix.Length..].Trim();
        }

        return secret;
    }

    private static string DefaultKeyName(Channel channel)
    {
        for (int n = channel.Keys.Count + 1; ; n++)
        {
            string candidate = "key-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!channel.Keys.Any(k => k.Name == candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task<(ChannelChanges? Changes, ValidationErrors Errors)> ValidateAsync(
        ConfigDbContext db,
        ChannelInput input,
        Channel? existing,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        string name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            errors.Add("name", "请填写渠道名。");
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add("name", "渠道名最多 200 个字符。");
        }
        else
        {
            int selfId = existing?.Id ?? 0;
            bool taken = await db.Channels
                .AnyAsync(c => c.Name == name && c.Id != selfId, cancellationToken)
                .ConfigureAwait(false);
            if (taken)
            {
                errors.Add("name", "已经有同名的渠道了。");
            }
        }

        ApiFormat format = input.ApiFormat ?? existing?.ApiFormat ?? ApiFormat.OpenAiChat;
        if (format is not (ApiFormat.OpenAiChat or ApiFormat.Anthropic or ApiFormat.OpenAiResponses))
        {
            errors.Add("apiFormat", "上游渠道只支持 OpenAI Chat、OpenAI Responses 和 Anthropic 三种格式。");
        }

        string? baseUrl = null;
        string? path = null;
        string rawBaseUrl = input.BaseUrl?.Trim() ?? "";
        string? rawPath = string.IsNullOrWhiteSpace(input.Path) ? null : input.Path.Trim();
        if (rawBaseUrl.Length == 0)
        {
            errors.Add("baseUrl", "请填写上游地址。");
        }
        else if (rawBaseUrl.Length > MaxUrlLength)
        {
            errors.Add("baseUrl", "上游地址太长了。");
        }
        else if (!UpstreamAddress.TryNormalizeBaseUrl(rawBaseUrl, out string? normalized, out string versionPrefix)
            || normalized is null)
        {
            errors.Add("baseUrl", "上游地址要是完整的 http:// 或 https:// 地址。");
        }
        else if (rawPath is { Length: > MaxUrlLength })
        {
            errors.Add("path", "请求路径太长了。");
        }
        else
        {
            // 粘贴 SDK 风格的 base_url（带 /v1、/api/v3）时，版本段并进路径，避免拼出 /v1/v1/...
            baseUrl = normalized;
            path = UpstreamAddress.ResolveChannelPath(rawPath, versionPrefix, format);
            if (!UpstreamAddress.TryBuildRequestUri(baseUrl, path, format, RequestKind.Chat, out _))
            {
                errors.Add("path", "拼不出一个合法的请求地址。");
            }
        }

        // Provider 必填（Q71）：目录里的供应商 id，或者明确选「不在列表里」（空字符串）。
        // 不自动留空，是为了让「这个渠道没有自动单价」是一个明确的选择。
        string? provider = existing?.Provider;
        if (input.Provider is not null)
        {
            provider = input.Provider.Trim().ToLowerInvariant();
            if (provider.Length > MaxProviderLength)
            {
                errors.Add("provider", "供应商 id 最多 100 个字符。");
            }
            else if (provider.Length > 0 && !ProviderId.IsMatch(provider))
            {
                errors.Add("provider", "供应商 id 只能有小写字母、数字、点、连字符和下划线。");
            }
        }
        else if (provider is null)
        {
            errors.Add("provider", "请选择供应商：它决定这个渠道按谁的价目表计价。不在列表里就选「不在列表里」。");
        }

        int priority = input.Priority ?? existing?.Priority ?? 0;
        if (priority is < AdminResults.MinPriority or > AdminResults.MaxPriority)
        {
            errors.Add("priority", "优先级要在 -1000 到 1000 之间。");
        }

        int weight = input.Weight ?? existing?.Weight ?? 1;
        if (weight is < AdminResults.MinWeight or > AdminResults.MaxWeight)
        {
            errors.Add("weight", "权重要在 1 到 1000 之间。");
        }

        int multiplierBasisPoints = existing?.MultiplierBasisPoints ?? 10_000;
        if (input.Multiplier is decimal multiplier)
        {
            if (multiplier is <= 0m or > MaxMultiplier)
            {
                errors.Add("multiplier", "倍率要大于 0、不超过 100。");
            }
            else
            {
                multiplierBasisPoints = Math.Max(1, (int)Math.Round(multiplier * 10_000m, MidpointRounding.AwayFromZero));
            }
        }

        List<string>? upstreamModels = null;
        if (input.UpstreamModels is not null)
        {
            upstreamModels = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? raw in input.UpstreamModels)
            {
                string model = raw?.Trim() ?? "";
                if (model.Length == 0)
                {
                    continue;
                }

                if (model.Length > MaxModelNameLength)
                {
                    errors.Add("upstreamModels", "上游模型名最多 300 个字符：" + model[..40] + "…");
                    continue;
                }

                if (seen.Add(model))
                {
                    upstreamModels.Add(model);
                }
            }

            if (upstreamModels.Count > MaxUpstreamModels)
            {
                errors.Add("upstreamModels", "一个渠道最多 2000 个上游模型。");
            }
        }
        else if (existing is null)
        {
            upstreamModels = [];
        }

        bool enabled = input.Enabled ?? existing?.Enabled ?? true;

        // 新渠道默认开同步（Q135）。
        bool modelSyncEnabled = input.ModelSyncEnabled ?? existing?.ModelSyncEnabled ?? true;

        // Anthropic 渠道的密钥放哪个头（Q166）。默认 x-api-key；其他格式存着也不起作用。
        bool anthropicBearerAuth = input.AnthropicBearerAuth ?? existing?.AnthropicBearerAuth ?? false;

        // OpenAI 入站转到 Anthropic 渠道时自动加缓存断点（Q247）。默认开；其他格式存着也不起作用。
        bool autoCacheBreakpoints = input.AutoCacheBreakpoints ?? existing?.AutoCacheBreakpoints ?? true;

        // 推理强度转到 Anthropic 渠道时思考参数的写法（ADR-0021）。默认自动；其他格式存着也不起作用。
        AnthropicThinkingStyle thinkingStyle = input.ThinkingStyle ?? existing?.ThinkingStyle ?? AnthropicThinkingStyle.Auto;

        // 密钥属于同一个账号（Q306）。默认关；只对 Responses 格式有意义。
        bool keysShareAccount = input.KeysShareAccount ?? existing?.KeysShareAccount ?? false;
        if (!Enum.IsDefined(thinkingStyle))
        {
            errors.Add("thinkingStyle", "没有这种思考参数写法。");
        }

        // 代理：null 不动；空白去掉；其余校验后整段加密存（Q130/Q131）。
        ProxyChange proxy = ProxyChange.Keep;
        if (input.ProxyUrl is not null)
        {
            if (string.IsNullOrWhiteSpace(input.ProxyUrl))
            {
                proxy = ProxyChange.Clear;
            }
            else if (ProxyAddress.TryParse(input.ProxyUrl, out ProxyAddress? parsed, out string? proxyError))
            {
                proxy = new ProxyChange(parsed.Value);
            }
            else
            {
                errors.Add("proxyUrl", proxyError);
            }
        }

        if (errors.HasErrors)
        {
            return (null, errors);
        }

        return (new ChannelChanges(name, baseUrl!, path, format, provider!, priority, weight, multiplierBasisPoints, enabled, upstreamModels, proxy, modelSyncEnabled, anthropicBearerAuth, autoCacheBreakpoints, thinkingStyle, keysShareAccount), errors);
    }

    private static void Apply(Channel channel, ChannelChanges changes, ChannelKeyProtector protector, DateTimeOffset now)
    {
        if (changes.Proxy.Changed)
        {
            channel.ProtectedProxyUrl = changes.Proxy.Url is null ? null : protector.ProtectProxy(changes.Proxy.Url);
        }

        channel.Name = changes.Name;
        channel.BaseUrl = changes.BaseUrl;
        channel.Path = changes.Path;
        channel.ApiFormat = changes.ApiFormat;
        channel.Provider = changes.Provider;
        channel.Priority = changes.Priority;
        channel.Weight = changes.Weight;
        channel.MultiplierBasisPoints = changes.MultiplierBasisPoints;
        channel.Enabled = changes.Enabled;
        channel.ModelSyncEnabled = changes.ModelSyncEnabled;
        channel.AnthropicBearerAuth = changes.AnthropicBearerAuth;
        channel.AutoCacheBreakpoints = changes.AutoCacheBreakpoints;
        channel.ThinkingStyle = changes.ThinkingStyle;
        channel.KeysShareAccount = changes.KeysShareAccount;
        channel.UpdatedAt = now;

        if (changes.UpstreamModels is null)
        {
            return;
        }

        // 按差异改：留下来的保留原来的创建时间和来源，新加的记为人工录入。
        var wanted = new HashSet<string>(changes.UpstreamModels, StringComparer.Ordinal);
        channel.UpstreamModels.RemoveAll(m => !wanted.Contains(m.Name));
        var present = new HashSet<string>(channel.UpstreamModels.Select(m => m.Name), StringComparer.Ordinal);
        foreach (string model in changes.UpstreamModels)
        {
            if (present.Add(model))
            {
                channel.UpstreamModels.Add(new UpstreamModel
                {
                    Name = model,
                    Source = UpstreamModelSource.Manual,
                    CreatedAt = now,
                });
            }
        }
    }

    private sealed record ChannelChanges(
        string Name,
        string BaseUrl,
        string? Path,
        ApiFormat ApiFormat,
        string Provider,
        int Priority,
        int Weight,
        int MultiplierBasisPoints,
        bool Enabled,
        IReadOnlyList<string>? UpstreamModels,
        ProxyChange Proxy,
        bool ModelSyncEnabled,
        bool AnthropicBearerAuth,
        bool AutoCacheBreakpoints,
        AnthropicThinkingStyle ThinkingStyle,
        bool KeysShareAccount);

    /// <summary>代理怎么改：不动、去掉，或者换成 <see cref="Url"/>（明文，存之前加密）。</summary>
    private sealed record ProxyChange(string? Url, bool Changed = true)
    {
        public static readonly ProxyChange Keep = new(null, Changed: false);
        public static readonly ProxyChange Clear = new((string?)null);
    }
}

internal static partial class CooldownLog
{
    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "Admin 手动解除了 {Count} 条冷却（渠道 {ChannelName}，ID {ChannelId}）")]
    public static partial void LogCooldownsCleared(this ILogger logger, int count, string channelName, int channelId);
}
