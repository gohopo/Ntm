using System.Collections.Concurrent;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Traffic;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 自动写法猜错时（Q315）：上游明说「这个模型不支持 enabled / adaptive」，就换另一种写法重发一次，
/// 并按渠道和上游模型记在内存里，之后直接用对的写法。重启后重新学；改渠道写法不受影响（只在自动时用）。
/// </summary>
public sealed class ThinkingStyleMemory
{
    private readonly ConcurrentDictionary<(int ChannelId, string UpstreamModel), AnthropicThinkingStyle> _learned = new();

    /// <summary>这个渠道上这个上游模型学到的写法；渠道不是自动写法时不用。</summary>
    public AnthropicThinkingStyle? Find(ChannelSnapshot channel, string upstreamModel) =>
        channel.ThinkingStyle == AnthropicThinkingStyle.Auto && _learned.TryGetValue((channel.ChannelId, upstreamModel), out AnthropicThinkingStyle style)
            ? style
            : null;

    public void Remember(int channelId, string upstreamModel, AnthropicThinkingStyle style) =>
        _learned[(channelId, upstreamModel)] = style;

    /// <summary>
    /// 计划写了哪种思考、被拒时换成哪种。渠道不是自动写法，或改写规则碰了 thinking / output_config
    /// （那时上游拒的是规则写的东西），都不换。
    /// </summary>
    internal static (AnthropicThinkingStyle? Fallback, string? Type) FallbackFor(
        IrRequest request, ChannelSnapshot channel, AnthropicThinkingStyle style, string upstreamModel)
    {
        if (channel.ThinkingStyle != AnthropicThinkingStyle.Auto
            || channel.Rewriter.TouchesTopLevel("thinking")
            || channel.Rewriter.TouchesTopLevel("output_config"))
        {
            return (null, null);
        }

        string? type = AnthropicThinking.Plan(request, style, upstreamModel).Type;
        return type switch
        {
            "enabled" => (AnthropicThinkingStyle.Adaptive, type),
            "adaptive" => (AnthropicThinkingStyle.Budget, type),
            _ => (null, null),
        };
    }

    /// <summary>上游的这次 400 是不是在拒绝计划写的那种思考。</summary>
    internal static bool RejectsThinking(UpstreamPlan plan, ForwardOutcome outcome)
    {
        if (plan.ThinkingFallback is null || outcome.FailureClass != FailureClass.ClientRequestError)
        {
            return false;
        }

        string text = (outcome.ErrorBody ?? string.Empty) + "\n" + (outcome.ErrorMessage ?? string.Empty);
        return plan.ThinkingType switch
        {
            "enabled" => text.Contains("thinking.type.enabled", StringComparison.OrdinalIgnoreCase)
                && text.Contains("not supported", StringComparison.OrdinalIgnoreCase),
            "adaptive" => text.Contains("adaptive thinking is not supported", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
