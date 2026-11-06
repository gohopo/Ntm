using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Rewrite;

namespace Ntm.Server.Admin;

/// <summary>一条请求改写规则（Q144–Q152）。</summary>
internal sealed record RewriteRuleView(
    RewriteTarget Target,
    RewriteOperation Operation,
    string Path,
    string? Value);

internal sealed record RewriteRulesInput(IReadOnlyList<RewriteRuleView>? Rules);

/// <summary>
/// 渠道的请求改写规则。和限制一样是渠道页上单独一段，所以单独一个路由（Q152）。
/// </summary>
internal static class RewriteRuleEndpoints
{
    public static RouteGroupBuilder MapChannelRewriteEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/{id:int}/rewrite-rules", SetAsync);
        return group;
    }

    public static IReadOnlyList<RewriteRuleView> ToView(Channel channel) =>
        [.. channel.RewriteRules.Select(r => new RewriteRuleView(r.Target, r.Operation, r.Path, r.Value))];

    private static async Task<IResult> SetAsync(
        int id,
        RewriteRulesInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return AdminResults.NotFound("渠道");
        }

        var errors = new ValidationErrors();
        List<ChannelRewriteRule> rules = Parse(input.Rules, errors);
        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        channel.RewriteRules.Clear();
        channel.RewriteRules.AddRange(rules);
        channel.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Ok(ToView(channel));
    }

    private static List<ChannelRewriteRule> Parse(IReadOnlyList<RewriteRuleView>? input, ValidationErrors errors)
    {
        if (input is null || input.Count == 0)
        {
            return [];
        }

        if (input.Count > ChannelRewriteRule.MaxRulesPerChannel)
        {
            errors.Add("rules", $"一个渠道最多 {ChannelRewriteRule.MaxRulesPerChannel} 条改写规则。");
            return [];
        }

        List<ChannelRewriteRule> rules = [];
        var seen = new HashSet<(RewriteTarget, string)>();
        for (int i = 0; i < input.Count; i++)
        {
            RewriteRuleView view = input[i];
            var rule = new ChannelRewriteRule
            {
                Target = view.Target,
                Operation = view.Operation,
                Path = view.Path?.Trim() ?? "",
                Value = view.Operation == RewriteOperation.Remove ? null : view.Value,
            };

            string prefix = AdminResults.Item("rules", i);
            if (RequestRewriter.Validate(rule) is { } problem)
            {
                errors.Add(prefix + "." + problem.Field, problem.Message);
                continue;
            }

            // 同一个位置写两条规则，后一条会盖掉前一条，看起来像没生效。直接拒掉。
            string key = rule.Target == RewriteTarget.Header ? rule.Path.ToLowerInvariant() : rule.Path;
            if (!seen.Add((rule.Target, key)))
            {
                errors.Add(prefix + ".path", "这个位置已经有一条规则了。");
                continue;
            }

            rules.Add(rule);
        }

        return rules;
    }
}
