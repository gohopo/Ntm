using System.Buffers;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Relay;

/// <summary>
/// OpenAI Responses 入站。Responses 上游同格式透传，只换 model（ADR-0002），store、include 按客户端原样（Q184）；
/// Chat 与 Anthropic 上游经 IR 转换，带着对方表达不了的参数时绕开那个候选（ADR-0014）。
/// 有状态的用法（previous_response_id、conversation、background、item_reference）只回到 Responses 上游，
/// 前两个还只回到产生它的落点（ADR-0016、ADR-0020）。<c>compact</c> 为 true 时是 <c>/v1/responses/compact</c>：
/// 只有 Responses 上游能做，产物不记落点（Q283）。
/// </summary>
internal sealed class ResponsesDialect(bool compact) : InboundDialect
{
    /// <summary>Anthropic 必填 max_tokens，Responses 的客户端常常不写，兜底用这个值（Q172）。</summary>
    private const int DefaultMaxTokens = 4096;

    public override ApiFormat Format => ApiFormat.OpenAiResponses;

    public override bool EchoesUpstreamClientErrors => true;

    public override string? UpstreamPathSuffix => compact ? "/compact" : null;

    public override bool RemembersResponseLandings => !compact;

    public override InboundParseResult Parse(byte[] body)
    {
        ResponsesRequestReading reading = ResponsesRequestConverter.Read(body);
        if (!reading.IsValid)
        {
            return new InboundParseResult(null, "请求体不是合法的 JSON 对象，或者缺少 model。");
        }

        if (reading.Rejected is { } rejected)
        {
            return new InboundParseResult(null, rejected);
        }

        return new InboundParseResult(new Request(body, reading, compact), null);
    }

    // Responses 的 HTTP 错误体和 Chat 一样是 {"error":{message,type,code}}。
    public override byte[] ErrorBody(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = OpenAiChatDialect.Describe(kind);
        return OpenAiErrorWriter.Body(message, type, code);
    }

    // 流里的错误是一个 error 事件。
    public override byte[] ErrorFrame(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = OpenAiChatDialect.Describe(kind);
        var buffer = new ArrayBufferWriter<byte>(256);
        ResponsesStreamEventWriter.WriteError(buffer, code ?? type, message);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class Request : InboundRequest
    {
        private readonly byte[] _body;
        private readonly string? _previousResponseId;
        private readonly string? _conversation;
        private readonly bool _background;
        private readonly bool _compact;
        private readonly Dictionary<string, UpstreamPlan> _plans = new(StringComparer.Ordinal);

        /// <summary>转成 IR 的结果，第一次被问到非 Responses 候选时才算（ADR-0014）。</summary>
        private ResponsesRequestConversion? _ir;

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Request(byte[] body, ResponsesRequestReading reading, bool compact)
        {
            _body = body;
            _previousResponseId = reading.PreviousResponseId;
            _conversation = reading.Conversation;
            _background = reading.IsBackground;
            _compact = compact;
            Model = reading.Model!;
            IsStream = reading.IsStream;
        }

        public override string? PreviousResponseId => _previousResponseId;

        public override string? UpstreamConversationId => _conversation;

        public override bool IsBackground => _background;

        public override bool CanBeServedBy(ApiFormat upstream, out string? reason)
        {
            reason = null;
            if (_compact && upstream != ApiFormat.OpenAiResponses)
            {
                reason = "compact 只有 Responses 格式的上游能做。";
                return false;
            }

            switch (upstream)
            {
                case ApiFormat.OpenAiResponses:
                    return true;

                case ApiFormat.OpenAiChat or ApiFormat.Anthropic:
                    {
                        ResponsesRequestConversion conversion = Convert();
                        reason = conversion.Request is { } ir
                            ? IrTargetCheck.Unrepresentable(ir, upstream)
                            : conversion.Unrepresentable ?? conversion.Error;
                        return reason is null;
                    }

                default:
                    reason = "这个上游格式还不支持。";
                    return false;
            }
        }

        public override UpstreamPlan PlanFor(ChannelSnapshot channel, string upstreamModel, Guid requestId, AnthropicThinkingStyle? thinkingStyle = null)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(upstreamModel);
            AnthropicThinkingStyle style = thinkingStyle ?? channel.ThinkingStyle;

            string cacheKey = (channel.Rewriter.HasBodyRules
                    ? channel.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : (int)channel.ApiFormat + (channel.AutoCacheBreakpoints ? "+cache" : string.Empty))
                + "+think" + (int)style + "\n" + upstreamModel;
            if (_plans.TryGetValue(cacheKey, out UpstreamPlan? cached))
            {
                return cached;
            }

            UpstreamPlan plan = channel.ApiFormat switch
            {
                ApiFormat.OpenAiResponses => BuildPassthroughPlan(channel, upstreamModel),
                ApiFormat.Anthropic => BuildAnthropicPlan(channel, upstreamModel, requestId, style),
                _ => BuildChatPlan(channel, upstreamModel, requestId),
            };
            _plans[cacheKey] = plan;
            return plan;
        }

        private ResponsesRequestConversion Convert() => _ir ??= ResponsesRequestConverter.ToIr(_body);

        private IrRequest Ir() => Convert().Request
            ?? throw new InvalidOperationException("这个请求转不成 IR，不该走到这里。");

        /// <summary>Responses 上游：原始字节照搬，只把 model 换成上游模型名（ADR-0002）。</summary>
        private UpstreamPlan BuildPassthroughPlan(ChannelSnapshot channel, string upstreamModel)
        {
            byte[] source = channel.Rewriter.ApplyToBody(_body);
            byte[] body = OpenAiChatRequestPatcher.TryReplaceModel(source, upstreamModel, out byte[] renamed)
                ? renamed
                : source;
            return new UpstreamPlan { Body = body };
        }

        private UpstreamPlan BuildChatPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId)
        {
            // OpenAiChatRequestWriter 在流式时自己写 include_usage，用量帧由翻译器消化，不需要再剔。
            byte[] body = channel.Rewriter.ApplyToBody(OpenAiChatRequestWriter.Write(Ir(), upstreamModel));
            return new UpstreamPlan { Body = body, Conversion = Conversion(requestId) };
        }

        private UpstreamPlan BuildAnthropicPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId, AnthropicThinkingStyle style)
        {
            byte[] body = channel.Rewriter.ApplyToBody(AnthropicRequestWriter.Write(Ir(), upstreamModel, channel.AutoCacheBreakpoints, style, channel.Rewriter.CacheBreakpointsWritten));
            if (AnthropicRequestPatcher.TryEnsureMaxTokens(body, DefaultMaxTokens, out byte[] patched))
            {
                body = patched;
            }

            (AnthropicThinkingStyle? fallback, string? thinkingType) = ThinkingStyleMemory.FallbackFor(Ir(), channel, style, upstreamModel);
            return new UpstreamPlan { Body = body, Conversion = Conversion(requestId), ThinkingFallback = fallback, ThinkingType = thinkingType };
        }

        // 用请求 ID 当响应 id：换候选重试后客户端看到的还是同一个（Q195）。
        private ResponseConversion Conversion(Guid requestId) => new()
        {
            MessageId = "resp_" + requestId.ToString("N"),
            FallbackModel = Model,
        };
    }
}
