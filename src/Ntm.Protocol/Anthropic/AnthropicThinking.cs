using System.Text.RegularExpressions;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>往 Anthropic 上游写思考参数用哪种写法（ADR-0021）。存在 Channel 上。</summary>
public enum AnthropicThinkingStyle
{
    /// <summary>按上游模型名里的 Claude 版本挑：4.6 及以后自适应，其余（包括认不出的名字）预算。</summary>
    Auto = 0,

    /// <summary>固定写 <c>thinking.type=enabled</c> 加 <c>budget_tokens</c>。</summary>
    Budget = 1,

    /// <summary>固定写 <c>thinking.type=adaptive</c> 加 <c>output_config.effort</c>。</summary>
    Adaptive = 2,
}

/// <summary>
/// 一次请求往 Anthropic 写的思考参数，以及因此要跟着改的 <c>max_tokens</c> 和采样参数。
/// <see cref="Type"/> 为空表示不写 <c>thinking</c>。
/// </summary>
public sealed record AnthropicThinkingPlan(
    string? Type,
    long? Budget,
    string? Effort,
    long? MaxTokens,
    bool DropTemperature,
    bool DropTopP,
    string? Display = null)
{
    public static readonly AnthropicThinkingPlan None = new(null, null, null, null, false, false);
}

/// <summary>
/// 推理强度和思考预算的互相换算（ADR-0021）。
/// 强度只是提示：表达不了就按规则降级，不避让候选也不报错。
/// </summary>
public static partial class AnthropicThinking
{
    /// <summary>Anthropic 允许的最小思考预算。</summary>
    public const long MinBudget = 1024;

    /// <summary>客户端给了输出上限时，留给回答的那部分；预算最多到上限减它。</summary>
    public const long AnswerReserve = 1024;

    /// <summary>预算写法、客户端没给上限时，<c>max_tokens</c> 取预算再加这么多。</summary>
    public const long BudgetReplyAllowance = 4096;

    /// <summary>自适应写法、客户端没给上限时补的 <c>max_tokens</c>。</summary>
    public const long AdaptiveDefaultMaxTokens = 32768;

    /// <summary>强度换预算（预算写法用）。认不出的强度返回空。</summary>
    public static long? BudgetFor(string effort) => effort.ToLowerInvariant() switch
    {
        "minimal" => 1024,
        "low" => 2048,
        "medium" => 8192,
        "high" => 24576,
        "xhigh" or "max" => 32768,
        _ => null,
    };

    /// <summary>
    /// 预算换强度（转到 OpenAI 格式用）：按正向表相邻两档的几何中点分，只出所有 OpenAI 模型都认的三档。
    /// 正向表的 low、medium、high 能原样换回来；xhigh、max 换回来是 high。
    /// </summary>
    public static string EffortForBudget(long budget) => budget switch
    {
        <= 4096 => "low",
        <= 16384 => "medium",
        _ => "high",
    };

    /// <summary>
    /// 往 OpenAI Chat 或 Responses 写的推理强度：客户端写了强度就照原文；
    /// 只有 Anthropic 的思考预算时按 <see cref="EffortForBudget"/> 分档；明确关掉思考时不写（有些模型不认 none）。
    /// </summary>
    public static string? OpenAiEffort(IrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReasoningEffort is { Length: > 0 } effort)
        {
            return effort;
        }

        return !request.ThinkingDisabled && request.ThinkingBudget is { } budget ? EffortForBudget(budget) : null;
    }

    /// <summary>把「自动」落到具体写法。</summary>
    public static AnthropicThinkingStyle Resolve(AnthropicThinkingStyle style, string upstreamModel)
    {
        if (style != AnthropicThinkingStyle.Auto)
        {
            return style;
        }

        return ClaudeVersion(upstreamModel) is { } version && (version.Major > 4 || (version.Major == 4 && version.Minor >= 6))
            ? AnthropicThinkingStyle.Adaptive
            : AnthropicThinkingStyle.Budget;
    }

    /// <summary>
    /// 从上游模型名里读 Claude 版本：<c>claude-opus-4-5-20251101</c> 是 4.5，<c>claude-3-7-sonnet</c> 是 3.7，
    /// <c>claude-sonnet-4-20250514</c> 是 4.0，<c>claude-opus-4.6</c> 是 4.6。
    /// 没有 <c>claude-</c> 前缀、但以系列名开头的也认（Q315）：<c>opus-4-7</c>、<c>sonnet-5</c>、<c>fable-5-1</c>。其余返回空。
    /// </summary>
    public static (int Major, int Minor)? ClaudeVersion(string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(upstreamModel);
        Match match = ClaudeVersionPattern().Match(upstreamModel.ToLowerInvariant());
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, out int major))
        {
            return null;
        }

        int minor = match.Groups[2].Success && int.TryParse(match.Groups[2].ValueSpan, out int m) ? m : 0;
        return (major, minor);
    }

    /// <summary>算出这次请求往 Anthropic 写什么。</summary>
    public static AnthropicThinkingPlan Plan(IrRequest request, AnthropicThinkingStyle style, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        AnthropicThinkingStyle resolved = Resolve(style, upstreamModel);
        string? effort = request.ReasoningEffort is { } text && IsKnownEffort(text) ? text.ToLowerInvariant() : null;

        return resolved == AnthropicThinkingStyle.Adaptive
            ? PlanAdaptive(request, effort)
            : PlanBudget(request, effort);
    }

    /// <summary>
    /// 预算写法开不了思考的两种情况：强制调用工具（只接受 auto 和 none，Q311），
    /// 以及工具循环里这一轮 assistant 开头没有能回传的思考块（Q312）。
    /// </summary>
    private static bool BudgetCannotThink(IrRequest request) =>
        request.ToolChoice is { Mode: IrToolChoiceMode.Any or IrToolChoiceMode.Specific }
        || TurnLacksLeadingThinking(request.Messages);

    /// <summary>
    /// 这一轮（最后一条真正的 user 消息之后）已经有 assistant 消息，而它写出来的第一块不是带签名的思考。
    /// 工具结果放在 user 消息里，不算一轮的开始。
    /// </summary>
    private static bool TurnLacksLeadingThinking(IReadOnlyList<IrMessage> messages)
    {
        int start = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == IrRole.User && !messages[i].Content.Any(b => b is IrToolResult))
            {
                start = i;
                break;
            }
        }

        for (int i = start + 1; i < messages.Count; i++)
        {
            if (messages[i].Role != IrRole.Assistant)
            {
                continue;
            }

            IrBlock? first = messages[i].Content.FirstOrDefault(AnthropicCacheBreakpoints.Writes);
            bool leadsWithThinking = first is IrRedactedReasoning
                || (first is IrReasoning { Text.Length: > 0 } reasoning && reasoning.SignedFor(ApiFormat.Anthropic));
            return !leadsWithThinking;
        }

        return false;
    }

    private static AnthropicThinkingPlan PlanBudget(IrRequest request, string? effort)
    {
        // none 和明确关闭：旧模型默认就不思考，什么都不写。
        if (request.ThinkingDisabled || effort == "none")
        {
            return AnthropicThinkingPlan.None;
        }

        long? wanted = request.ThinkingBudget ?? (effort is null ? null : BudgetFor(effort));
        if (wanted is not { } budget || BudgetCannotThink(request))
        {
            return AnthropicThinkingPlan.None;
        }

        budget = Math.Max(budget, MinBudget);
        long maxTokens;
        if (request.MaxOutputTokens is { } clientMax)
        {
            // 客户端的上限包含思考，照用不调大；预算让出回答的那一截，不够最小预算就不开。
            budget = Math.Min(budget, clientMax - AnswerReserve);
            if (budget < MinBudget)
            {
                return AnthropicThinkingPlan.None;
            }

            maxTokens = clientMax;
        }
        else
        {
            maxTokens = budget + BudgetReplyAllowance;
        }

        bool keepTopP = request.TopP is { } topP && topP >= 0.95 && topP <= 1;
        return new AnthropicThinkingPlan("enabled", budget, null, maxTokens, DropTemperature: true, DropTopP: !keepTopP);
    }

    private static AnthropicThinkingPlan PlanAdaptive(IrRequest request, string? effort)
    {
        // 自适应写法一律不写 temperature 和 top_p：4.7 及以后的模型给了非默认值就 400。
        if (request.ThinkingDisabled || effort == "none")
        {
            // 新模型发 disabled 会 400，退一步只压低强度。
            return new AnthropicThinkingPlan(null, null, "low", null, DropTemperature: true, DropTopP: true);
        }

        string? target = effort switch
        {
            null => request.ThinkingBudget is { } budget ? EffortForBudget(budget) : null,
            "minimal" => "low",
            _ => effort,
        };

        if (target is null)
        {
            return new AnthropicThinkingPlan(null, null, null, null, DropTemperature: true, DropTopP: true);
        }

        long maxTokens = request.MaxOutputTokens ?? AdaptiveDefaultMaxTokens;
        // Claude 5 起默认只回签名不回思考文字；转过来的客户端要看推理，明确要摘要（Q313）。
        return new AnthropicThinkingPlan("adaptive", null, target, maxTokens, DropTemperature: true, DropTopP: true, Display: "summarized");
    }

    private static bool IsKnownEffort(string effort) => effort.ToLowerInvariant() is
        "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";

    [GeneratedRegex(@"(?:claude-(?:[a-z]+-)?|(?<![a-z])(?:opus|sonnet|haiku|fable|mythos)-)(\d+)(?:[-.](\d{1,2})(?!\d))?", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeVersionPattern();
}
