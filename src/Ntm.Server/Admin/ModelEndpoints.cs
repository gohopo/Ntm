using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Server.Admin;

/// <summary>一条匹配规则。<see cref="ExcludedPatterns"/> 按这条规则自己的匹配方式解释。</summary>
internal sealed record RuleInput(
    MatchKind? Kind,
    string? Pattern,
    int? ChannelId,
    IReadOnlyList<int>? ExcludedChannelIds,
    IReadOnlyList<string?>? ExcludedPatterns,
    int? Priority,
    int? Weight,
    bool? Enabled);

/// <summary>保存时整体替换规则：列表里的顺序就是规则的先后（先到先得）。</summary>
internal sealed record ModelInput(
    string? Name,
    string? DisplayName,
    RequestKind? RequestKind,
    bool? Enabled,
    IReadOnlyList<RuleInput?>? Rules);

internal sealed record ModelView(
    int Id,
    string Name,
    string? DisplayName,
    RequestKind RequestKind,
    bool Enabled,
    int RuleCount,
    int CandidateCount,
    int ProblemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record RuleView(
    int Ordinal,
    MatchKind Kind,
    string Pattern,
    int? ChannelId,
    IReadOnlyList<int> ExcludedChannelIds,
    IReadOnlyList<string> ExcludedPatterns,
    int? Priority,
    int? Weight,
    bool Enabled);

internal sealed record ModelDetailView(ModelView Model, IReadOnlyList<RuleView> Rules, RoutePreviewView Route);

/// <summary><see cref="ChannelPriority"/> 是渠道自己的优先级；和所在档位不同说明规则改了它（Q266）。</summary>
internal sealed record CandidateView(int ChannelId, string ChannelName, string UpstreamModel, int Weight, int RuleOrdinal, int ChannelPriority = 0);

/// <summary>
/// 一个候选此刻的状态（Q259）。<see cref="Block"/> 为 null 表示可用；<see cref="Share"/> 只在第一个有可用候选的档里有值，
/// 是按有效权重算的预计份额（0–1），其余档是备用（Q260）。
/// </summary>
internal sealed record RoutingCandidateView(
    int ChannelId,
    string ChannelName,
    string UpstreamModel,
    int ChannelPriority,
    int RuleOrdinal,
    int Weight,
    int Successes,
    int Failures,
    double HealthFactor,
    int EffectiveWeight,
    double? Share,
    BlockReason? Block,
    DateTimeOffset? RecoversAt);

internal sealed record RoutingTierView(int Priority, bool Active, IReadOnlyList<RoutingCandidateView> Candidates);

/// <summary>「现在的选路」（Q259）：只读，不推进轮询，不占探测名额。</summary>
internal sealed record RoutingStateView(bool ModelEnabled, DateTimeOffset At, IReadOnlyList<RoutingTierView> Tiers);

internal sealed record PriorityGroupView(int Priority, IReadOnlyList<CandidateView> Candidates);

/// <summary>每条规则实际匹配到几个落点；<see cref="EffectivePattern"/> 是锚定之后真正拿去匹配的正则。</summary>
internal sealed record RulePreviewView(int Ordinal, bool Enabled, string? EffectivePattern, string? Problem, int MatchCount);

/// <summary>规则能匹配上、但因为渠道本身的状态没进候选的落点。</summary>
internal sealed record ExcludedCandidateView(int ChannelId, string ChannelName, string UpstreamModel, string Reason);

internal sealed record RoutePreviewView(
    int CandidateCount,
    IReadOnlyList<PriorityGroupView> PriorityGroups,
    IReadOnlyList<RulePreviewView> Rules,
    IReadOnlyList<ExcludedCandidateView> Excluded);

/// <summary>/api/models：下发模型和它的匹配规则，外加用真实匹配器跑的预览。</summary>
internal static class ModelEndpoints
{
    private const int MaxNameLength = 300;
    private const int MaxPatternLength = 1000;
    private const int MaxRules = 200;
    private const int MaxExcludedShown = 200;

    public static RouteGroupBuilder MapModelEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/preview", PreviewAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/routing", RoutingAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPatch("/{id:int}", SetEnabledAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        List<PublishedModel> models = await db.PublishedModels
            .AsNoTracking()
            .Include(m => m.Rules)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, PublishedModelRoute> routes = snapshots.Current.Routes.ToDictionary(r => r.PublishedModelId);
        return TypedResults.Ok(models
            .Select(m => ToView(m, routes.GetValueOrDefault(m.Id)))
            .ToList());
    }

    private static async Task<IResult> GetAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        PublishedModel? model = await db.PublishedModels
            .AsNoTracking()
            .Include(m => m.Rules)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            return AdminResults.NotFound("下发模型");
        }

        return TypedResults.Ok(await ToDetailAsync(db, model, snapshots, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateAsync(
        ModelInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        (ModelChanges? changes, ValidationErrors errors) =
            await ValidateAsync(db, input, existingId: null, cancellationToken).ConfigureAwait(false);
        if (changes is null)
        {
            return errors.ToResult();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var model = new PublishedModel
        {
            Name = changes.Name,
            DisplayName = changes.DisplayName,
            RequestKind = changes.RequestKind,
            Enabled = changes.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        model.Rules.AddRange(changes.Rules);
        db.PublishedModels.Add(model);
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的下发模型了。");
        }

        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        ModelDetailView detail = await ToDetailAsync(db, model, snapshots, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(AdminResults.Location("/api/models", model.Id), detail);
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        ModelInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        PublishedModel? model = await db.PublishedModels
            .Include(m => m.Rules)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            return AdminResults.NotFound("下发模型");
        }

        (ModelChanges? changes, ValidationErrors errors) =
            await ValidateAsync(db, input, existingId: model.Id, cancellationToken).ConfigureAwait(false);
        if (changes is null)
        {
            return errors.ToResult();
        }

        // API 密钥的模型限制存的是下发模型名（客户端认的就是名字），改名时跟着改，
        // 否则改名一次就悄悄把这些密钥挡在外面。删模型时不清：留着失效的名字无害。
        if (!string.Equals(model.Name, changes.Name, StringComparison.Ordinal))
        {
            string oldName = model.Name;
            List<ApiKey> apiKeys = await db.ApiKeys.ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (ApiKey apiKey in apiKeys)
            {
                int index = apiKey.AllowedModels.IndexOf(oldName);
                if (index >= 0)
                {
                    apiKey.AllowedModels[index] = changes.Name;
                }
            }
        }

        model.Name = changes.Name;
        model.DisplayName = changes.DisplayName;
        model.RequestKind = changes.RequestKind;
        model.Enabled = changes.Enabled;
        model.UpdatedAt = timeProvider.GetUtcNow();

        // 规则整体替换：规则没有别的东西引用它，按顺序重写最直接。
        db.MatchRules.RemoveRange(model.Rules);
        model.Rules.Clear();
        model.Rules.AddRange(changes.Rules);
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的下发模型了。");
        }

        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(await ToDetailAsync(db, model, snapshots, cancellationToken).ConfigureAwait(false));
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

        PublishedModel? model = await db.PublishedModels
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            return AdminResults.NotFound("下发模型");
        }

        model.Enabled = enabled;
        model.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        PublishedModel? model = await db.PublishedModels
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            return AdminResults.NotFound("下发模型");
        }

        // 规则由外键级联删除。
        db.PublishedModels.Remove(model);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// 不保存，只回答「按这些规则，现在会路由到哪里」。用的就是选路时那份代码和当前快照，
    /// 所以预览和真实行为不会对不上（AxonHub 的做法，Q36）；前端不自己实现匹配。
    /// 草稿不做严格校验：写到一半的规则照样能预览，问题按规则逐条报出来。
    /// </summary>
    private static async Task<IResult> PreviewAsync(
        ModelInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        PublishedModel draft = BuildDraft(input);
        PublishedModelRoute route = ConfigSnapshotProvider.BuildRoutes([draft], snapshots.Current.Channels)[0];
        IReadOnlyList<ExcludedCandidateView> excluded =
            await ExplainExcludedAsync(db, draft, route, snapshots.Current, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToPreview(draft.Rules, route, excluded));
    }

    private static async Task<IResult> RoutingAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CandidateSelector selector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        PublishedModel? model = await db.PublishedModels
            .AsNoTracking()
            .Include(m => m.Rules)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            return AdminResults.NotFound("下发模型");
        }

        ConfigSnapshot snapshot = snapshots.Current;
        PublishedModelRoute route = snapshot.Routes.FirstOrDefault(r => r.PublishedModelId == model.Id)
            ?? ConfigSnapshotProvider.BuildRoutes([model], snapshot.Channels)[0];
        IReadOnlyList<CandidateState> states = selector.Inspect(route);

        List<RoutingTierView> tiers = [];
        bool activeFound = false;
        int index = 0;
        foreach (PriorityGroup group in route.PriorityGroups)
        {
            List<CandidateState> members = [.. states.Skip(index).Take(group.Candidates.Count)];
            index += group.Candidates.Count;

            bool active = !activeFound && members.Any(m => m.Block is null);
            activeFound |= active;
            int total = members.Where(m => m.Block is null).Sum(m => m.EffectiveWeight);

            tiers.Add(new RoutingTierView(
                group.Priority,
                active,
                [.. members.Select(m => new RoutingCandidateView(
                    m.Candidate.ChannelId,
                    m.Candidate.Channel.ChannelName,
                    m.Candidate.UpstreamModel,
                    m.Candidate.Channel.Priority,
                    m.Candidate.RuleOrdinal,
                    m.Candidate.Weight,
                    m.Health.Successes,
                    m.Health.Failures,
                    m.Health.Factor,
                    m.EffectiveWeight,
                    active && m.Block is null && total > 0 ? (double)m.EffectiveWeight / total : null,
                    m.Block?.Reason,
                    m.Block?.RecoversAt))]));
        }

        return TypedResults.Ok(new RoutingStateView(model.Enabled, timeProvider.GetUtcNow(), tiers));
    }

    private static async Task<ModelDetailView> ToDetailAsync(
        ConfigDbContext db,
        PublishedModel model,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        ConfigSnapshot snapshot = snapshots.Current;
        PublishedModelRoute route = snapshot.Routes.FirstOrDefault(r => r.PublishedModelId == model.Id)
            ?? ConfigSnapshotProvider.BuildRoutes([model], snapshot.Channels)[0];
        IReadOnlyList<ExcludedCandidateView> excluded =
            await ExplainExcludedAsync(db, model, route, snapshot, cancellationToken).ConfigureAwait(false);

        return new ModelDetailView(
            ToView(model, route),
            [.. model.Rules.OrderBy(r => r.Ordinal).Select(r => new RuleView(
                r.Ordinal,
                r.Kind,
                r.Pattern,
                r.ChannelId,
                r.ExcludedChannelIds,
                r.ExcludedPatterns,
                r.Priority,
                r.Weight,
                r.Enabled))],
            ToPreview(model.Rules, route, excluded));
    }

    private static ModelView ToView(PublishedModel model, PublishedModelRoute? route) =>
        new(
            model.Id,
            model.Name,
            model.DisplayName,
            model.RequestKind,
            model.Enabled,
            model.Rules.Count,
            route?.CandidateCount ?? 0,
            route?.Problems.Count ?? 0,
            model.CreatedAt,
            model.UpdatedAt);

    private static RoutePreviewView ToPreview(
        IReadOnlyList<MatchRule> rules,
        PublishedModelRoute route,
        IReadOnlyList<ExcludedCandidateView> excluded)
    {
        Dictionary<int, string> problems = route.Problems
            .GroupBy(p => p.RuleOrdinal)
            .ToDictionary(g => g.Key, g => g.First().Message);
        List<RouteCandidate> candidates = [.. route.PriorityGroups.SelectMany(t => t.Candidates)];

        return new RoutePreviewView(
            route.CandidateCount,
            [.. route.PriorityGroups.Select(t => new PriorityGroupView(
                t.Priority,
                [.. t.Candidates.Select(c => new CandidateView(
                    c.ChannelId,
                    c.Channel.ChannelName,
                    c.UpstreamModel,
                    c.Weight,
                    c.RuleOrdinal,
                    c.Channel.Priority))]))],
            [.. rules.OrderBy(r => r.Ordinal).Select(r => new RulePreviewView(
                r.Ordinal,
                r.Enabled,
                EffectivePattern(r),
                problems.GetValueOrDefault(r.Ordinal),
                candidates.Count(c => c.RuleOrdinal == r.Ordinal)))],
            excluded);
    }

    /// <summary>只有正则才需要给人看锚定之后的样子；精确名就是它自己。</summary>
    private static string? EffectivePattern(MatchRule rule) =>
        rule.Kind == MatchKind.Regex && ModelPattern.TryCreate(MatchKind.Regex, rule.Pattern, out ModelPattern? pattern, out _)
            ? pattern?.Effective
            : null;

    /// <summary>
    /// 规则对着全部渠道（包括停用的、没密钥的）再匹配一遍：能匹配上却不在候选里的落点，说明是渠道状态的原因。
    /// 先到先得是按 (渠道, 上游模型) 记的，多出来的渠道不会抢走真实候选的落点。
    /// </summary>
    private static async Task<IReadOnlyList<ExcludedCandidateView>> ExplainExcludedAsync(
        ConfigDbContext db,
        PublishedModel model,
        PublishedModelRoute route,
        ConfigSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        List<Channel> channels = await db.Channels
            .AsNoTracking()
            .Include(c => c.Keys)
            .Include(c => c.UpstreamModels)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        MatchResult everything = MatchRuleEvaluator.Evaluate(
            model.Rules,
            [.. channels.Select(c => new MatchableChannel
            {
                ChannelId = c.Id,
                ChannelName = c.Name,
                Priority = c.Priority,
                Weight = c.Weight,
                UpstreamModels = [.. c.UpstreamModels.Select(m => m.Name).Order(StringComparer.Ordinal)],
            })]);

        HashSet<(int, string)> served = [.. route.PriorityGroups
            .SelectMany(t => t.Candidates)
            .Select(c => (c.ChannelId, c.UpstreamModel))];
        Dictionary<int, Channel> byId = channels.ToDictionary(c => c.Id);
        Dictionary<int, ChannelSnapshot> live = snapshot.Channels.ToDictionary(c => c.ChannelId);

        return [.. everything.Candidates
            .Where(c => !served.Contains((c.ChannelId, c.UpstreamModel)))
            .Take(MaxExcludedShown)
            .Select(c => new ExcludedCandidateView(
                c.ChannelId,
                byId[c.ChannelId].Name,
                c.UpstreamModel,
                ExclusionReason(byId[c.ChannelId], live.GetValueOrDefault(c.ChannelId), model.RequestKind)))];
    }

    private static string ExclusionReason(Channel channel, ChannelSnapshot? live, RequestKind kind)
    {
        if (!channel.Enabled)
        {
            return "渠道已停用。";
        }

        if (!channel.Keys.Any(k => k.Enabled))
        {
            return "渠道没有启用的密钥。";
        }

        if (!ApiFormatCapability.Supports(channel.ApiFormat, kind))
        {
            return kind == RequestKind.Chat
                ? "这个渠道的 API 格式服务不了这类请求。"
                : "嵌入和图片只能用 OpenAI Chat 格式的渠道。";
        }

        if (kind != RequestKind.Chat && !UpstreamAddress.TryBuildRequestUri(channel.BaseUrl, channel.Path, channel.ApiFormat, kind, out _))
        {
            return "渠道配了自定义路径，推不出这类端点（路径以 /chat/completions 结尾才能推）。";
        }

        // 库里有启用的密钥、快照里却一把都没有：都解不开，多半是 Data Protection 密钥环换过。
        return live is { Keys.Count: 0 } ? "渠道的密钥都解不开（密钥环可能换过）。" : "渠道的改动还没生效。";
    }

    private static PublishedModel BuildDraft(ModelInput input)
    {
        var draft = new PublishedModel
        {
            Name = string.IsNullOrWhiteSpace(input.Name) ? "(预览)" : input.Name.Trim(),
            DisplayName = input.DisplayName,
            RequestKind = input.RequestKind is RequestKind kind && Publishable(kind) ? kind : RequestKind.Chat,
            Enabled = true,
        };

        IReadOnlyList<RuleInput?> rules = input.Rules ?? [];
        for (int i = 0; i < rules.Count && i < MaxRules; i++)
        {
            if (rules[i] is not RuleInput rule)
            {
                continue;
            }

            var draftRule = new MatchRule
            {
                Ordinal = i + 1,
                Kind = rule.Kind ?? MatchKind.Exact,
                Pattern = rule.Pattern?.Trim() ?? "",
                ChannelId = rule.ChannelId,
                Priority = rule.Priority,
                Weight = rule.Weight,
                Enabled = rule.Enabled ?? true,
            };
            draftRule.ExcludedChannelIds.AddRange(rule.ExcludedChannelIds ?? []);
            draftRule.ExcludedPatterns.AddRange((rule.ExcludedPatterns ?? [])
                .Select(p => p?.Trim() ?? "")
                .Where(p => p.Length > 0));
            draft.Rules.Add(draftRule);
        }

        return draft;
    }

    private static async Task<(ModelChanges? Changes, ValidationErrors Errors)> ValidateAsync(
        ConfigDbContext db,
        ModelInput input,
        int? existingId,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        string name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            errors.Add("name", "请填写模型名。");
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add("name", "模型名最多 300 个字符。");
        }
        else if (name.Any(char.IsWhiteSpace))
        {
            errors.Add("name", "模型名里不能有空格。");
        }
        else
        {
            // 名字区分大小写：客户端写的 model 字段按原样比较。
            int selfId = existingId ?? 0;
            bool taken = await db.PublishedModels
                .AnyAsync(m => m.Name == name && m.Id != selfId, cancellationToken)
                .ConfigureAwait(false);
            if (taken)
            {
                errors.Add("name", "已经有同名的下发模型了。");
            }
        }

        string? displayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim();
        if (displayName is { Length: > MaxNameLength })
        {
            errors.Add("displayName", "显示名最多 300 个字符。");
        }

        RequestKind requestKind = input.RequestKind ?? RequestKind.Chat;
        if (!Publishable(requestKind))
        {
            errors.Add("requestKind", "下发模型只能是对话、嵌入或图片类。");
        }

        IReadOnlyList<RuleInput?> inputs = input.Rules ?? [];
        if (inputs.Count > MaxRules)
        {
            errors.Add("rules", "一个下发模型最多 200 条规则。");
        }

        List<int> channelIdList = await db.Channels.Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> channelIds = [.. channelIdList];
        var rules = new List<MatchRule>(inputs.Count);
        for (int i = 0; i < inputs.Count && i < MaxRules; i++)
        {
            string field = AdminResults.Item("rules", i);
            if (inputs[i] is not RuleInput ruleInput)
            {
                errors.Add(field, "规则不能为空。");
                continue;
            }

            if (ParseRule(ruleInput, i + 1, field, channelIds, errors) is MatchRule rule)
            {
                rules.Add(rule);
            }
        }

        if (errors.HasErrors)
        {
            return (null, errors);
        }

        // 新建的模型默认不下发：下发什么是人的决定（Q35）。
        bool enabled = input.Enabled ?? false;
        return (new ModelChanges(name, displayName, requestKind, enabled, rules), errors);
    }

    private static MatchRule? ParseRule(
        RuleInput input,
        int ordinal,
        string field,
        HashSet<int> channelIds,
        ValidationErrors errors)
    {
        int before = ErrorCount(errors);
        MatchKind kind = input.Kind ?? MatchKind.Exact;

        string pattern = input.Pattern?.Trim() ?? "";
        if (pattern.Length > MaxPatternLength)
        {
            errors.Add(field + ".pattern", "模式最多 1000 个字符。");
        }
        else if (!ModelPattern.TryCreate(kind, pattern, out _, out string? problem))
        {
            errors.Add(field + ".pattern", problem ?? "模式无效。");
        }

        if (input.ChannelId is int channelId && !channelIds.Contains(channelId))
        {
            errors.Add(field + ".channelId", "指定的渠道不存在。");
        }

        List<int> excludedChannels = [.. (input.ExcludedChannelIds ?? []).Distinct()];
        if (excludedChannels.Any(id => !channelIds.Contains(id)))
        {
            errors.Add(field + ".excludedChannelIds", "要排除的渠道里有不存在的。");
        }

        List<string> excludedPatterns = [];
        IReadOnlyList<string?> rawExcluded = input.ExcludedPatterns ?? [];
        for (int j = 0; j < rawExcluded.Count; j++)
        {
            string excludedPattern = rawExcluded[j]?.Trim() ?? "";
            if (excludedPattern.Length == 0)
            {
                continue;
            }

            string excludedField = AdminResults.Item(field + ".excludedPatterns", j);
            if (excludedPattern.Length > MaxPatternLength)
            {
                errors.Add(excludedField, "模式最多 1000 个字符。");
            }
            else if (!ModelPattern.TryCreate(kind, excludedPattern, out _, out string? problem))
            {
                // 排除模式按这条规则自己的匹配方式解释：精确规则排除精确名，正则规则排除正则。
                errors.Add(excludedField, problem ?? "模式无效。");
            }
            else
            {
                excludedPatterns.Add(excludedPattern);
            }
        }

        if (input.Priority is int priority && priority is < AdminResults.MinPriority or > AdminResults.MaxPriority)
        {
            errors.Add(field + ".priority", "优先级要在 -1000 到 1000 之间。");
        }

        if (input.Weight is int weight && weight is < AdminResults.MinWeight or > AdminResults.MaxWeight)
        {
            errors.Add(field + ".weight", "权重要在 1 到 1000 之间。");
        }

        if (ErrorCount(errors) != before)
        {
            return null;
        }

        var rule = new MatchRule
        {
            Ordinal = ordinal,
            Kind = kind,
            Pattern = pattern,
            ChannelId = input.ChannelId,
            Priority = input.Priority,
            Weight = input.Weight,
            Enabled = input.Enabled ?? true,
        };
        rule.ExcludedChannelIds.AddRange(excludedChannels);
        rule.ExcludedPatterns.AddRange(excludedPatterns);
        return rule;
    }

    private static int ErrorCount(ValidationErrors errors) => errors.Count;

    /// <summary>能建成下发模型的请求类型（Q220/Q222）：音频和重排还没有端点。</summary>
    private static bool Publishable(RequestKind kind) =>
        kind is RequestKind.Chat or RequestKind.Embedding or RequestKind.Image;

    private sealed record ModelChanges(string Name, string? DisplayName, RequestKind RequestKind, bool Enabled, List<MatchRule> Rules);
}
