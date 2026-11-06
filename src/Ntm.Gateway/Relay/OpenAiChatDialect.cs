using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Relay;

/// <summary>
/// OpenAI Chat 入站。OpenAI Chat 上游同格式透传（ADR-0002）；
/// Anthropic 与 Responses 上游经 IR 转换，带着对方表达不了的参数时绕开那个候选（Q171、ADR-0014）。
/// </summary>
internal sealed class OpenAiChatDialect : InboundDialect
{
    public override ApiFormat Format => ApiFormat.OpenAiChat;

    public override bool EchoesUpstreamClientErrors => true;

    /// <summary>Anthropic 必填 max_tokens 而 OpenAI 的客户端常常不写，兜底用这个值（Q172）。</summary>
    private const int DefaultMaxTokens = 4096;

    public override InboundParseResult Parse(byte[] body)
    {
        OpenAiChatRequestReading reading = OpenAiChatRequestReader.Read(body);
        if (!reading.IsValid)
        {
            return new InboundParseResult(null, "请求体不是合法的 JSON 对象。");
        }

        if (string.IsNullOrEmpty(reading.Model))
        {
            return new InboundParseResult(null, "请求里缺少 model。");
        }

        return new InboundParseResult(new Request(body, reading), null);
    }

    public override byte[] ErrorBody(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = Describe(kind);
        return OpenAiErrorWriter.Body(message, type, code);
    }

    public override byte[] ErrorFrame(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = Describe(kind);
        return OpenAiErrorWriter.StreamFrame(message, type, code);
    }

    internal static (string Type, string? Code) Describe(GatewayErrorKind kind) => kind switch
    {
        GatewayErrorKind.MissingApiKey or GatewayErrorKind.InvalidApiKey => ("invalid_request_error", "invalid_api_key"),
        GatewayErrorKind.BodyTooLarge or GatewayErrorKind.InvalidRequest => ("invalid_request_error", null),
        GatewayErrorKind.NoChannel => ("server_error", "no_available_channel"),
        GatewayErrorKind.ModelNotFound => ("invalid_request_error", "model_not_found"),
        GatewayErrorKind.AllCooling => ("server_error", "channels_cooling"),
        GatewayErrorKind.QuotaExhausted => ("insufficient_quota", "insufficient_quota"),
        GatewayErrorKind.ModelNotAllowed => ("permission_error", "model_not_allowed"),
        GatewayErrorKind.ResourceNotFound => ("invalid_request_error", "not_found"),
        GatewayErrorKind.ChannelNotAllowed => ("permission_error", "channel_not_allowed"),
        GatewayErrorKind.Interrupted => ("permission_error", "request_interrupted"),
        GatewayErrorKind.ShuttingDown => ("server_error", "gateway_shutting_down"),
        GatewayErrorKind.RateLimited => ("rate_limit_error", "rate_limit_exceeded"),
        GatewayErrorKind.Timeout => ("server_error", "upstream_timeout"),
        GatewayErrorKind.UpstreamFailed => ("server_error", "upstream_failed"),
        GatewayErrorKind.UpstreamInterrupted => ("server_error", "upstream_interrupted"),
        _ => ("server_error", null),
    };

    private sealed class Request : InboundRequest
    {
        private readonly byte[] _body;
        private readonly bool _clientWantsUsage;
        private readonly Dictionary<string, UpstreamPlan> _plans = [];

        /// <summary>转成 IR 的结果，第一次被问到 Anthropic 候选时才算，之后复用（ADR-0014）。</summary>
        private OpenAiChatRequestConversion? _ir;

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Request(byte[] body, OpenAiChatRequestReading reading)
        {
            _body = body;
            _clientWantsUsage = reading.ClientWantsUsage;
            Model = reading.Model!;
            IsStream = reading.IsStream;
        }

        public override bool CanBeServedBy(ApiFormat upstream, out string? reason)
        {
            reason = null;
            switch (upstream)
            {
                case ApiFormat.OpenAiChat:
                    return true;

                case ApiFormat.Anthropic or ApiFormat.OpenAiResponses:
                    {
                        OpenAiChatRequestConversion conversion = Convert();
                        reason = conversion.Request is { } ir
                            ? IrTargetCheck.Unrepresentable(ir, upstream)
                            : conversion.Unrepresentable ?? conversion.Error;
                        return reason is null;
                    }

                default:
                    return false;
            }
        }

        public override UpstreamPlan PlanFor(ChannelSnapshot channel, string upstreamModel, Guid requestId, AnthropicThinkingStyle? thinkingStyle = null)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(upstreamModel);
            AnthropicThinkingStyle style = thinkingStyle ?? channel.ThinkingStyle;

            // 字节跟上游格式、上游模型名和渠道的改写规则有关：没有规则的渠道共用一份（Q149）。
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
                ApiFormat.Anthropic => BuildAnthropicPlan(channel, upstreamModel, requestId, style),
                ApiFormat.OpenAiResponses => BuildResponsesPlan(channel, upstreamModel, requestId),
                _ => BuildPlan(channel, upstreamModel),
            };
            _plans[cacheKey] = plan;
            return plan;
        }

        private OpenAiChatRequestConversion Convert() =>
            _ir ??= OpenAiChatRequestConverter.ToIr(_body);

        private UpstreamPlan BuildPlan(ChannelSnapshot channel, string upstreamModel)
        {
            // 渠道改写规则先跑，网关自己的改写后跑：规则改不掉选路和计费依赖的字段（Q149）。
            byte[] source = channel.Rewriter.ApplyToBody(_body);

            // 客户端说的是 PublishedModel，上游认的是 UpstreamModel：只把 model 这一个值换掉。
            byte[] body = OpenAiChatRequestPatcher.TryReplaceModel(source, upstreamModel, out byte[] renamed)
                ? renamed
                : source;

            // 流式且客户端没要 usage：注入 include_usage 拿到用量，转发时再把多出来的那一帧剔掉（ADR-0002）。
            if (IsStream
                && !_clientWantsUsage
                && OpenAiChatRequestPatcher.TryEnsureIncludeUsage(body, out byte[] patched))
            {
                return new UpstreamPlan { Body = patched, SwallowUsageOnlyFrames = true };
            }

            return new UpstreamPlan { Body = body };
        }

        /// <summary>Responses 上游：从 IR 写出 Responses 请求体（store:false，Q184），再按规则改写。</summary>
        private UpstreamPlan BuildResponsesPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId)
        {
            IrRequest ir = Convert().Request
                ?? throw new InvalidOperationException("这个请求转不成 Responses 格式，不该走到这里。");

            byte[] body = channel.Rewriter.ApplyToBody(ResponsesRequestWriter.Write(ir, upstreamModel, PromptCacheKey));
            return new UpstreamPlan
            {
                Body = body,
                Conversion = new ResponseConversion
                {
                    MessageId = "chatcmpl-" + requestId.ToString("N"),
                    FallbackModel = Model,
                    ClientWantsUsage = _clientWantsUsage,
                },
            };
        }

        /// <summary>Anthropic 上游：从 IR 写出 Anthropic 请求体，再按规则改写，最后兜底补 max_tokens（Q172）。</summary>
        private UpstreamPlan BuildAnthropicPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId, AnthropicThinkingStyle style)
        {
            IrRequest ir = Convert().Request
                ?? throw new InvalidOperationException("这个请求转不成 Anthropic 格式，不该走到这里。");

            // 规则作用在转换后、真正发往上游的那份请求上，路径按上游格式写（Q150）。
            byte[] body = channel.Rewriter.ApplyToBody(AnthropicRequestWriter.Write(ir, upstreamModel, channel.AutoCacheBreakpoints, style, channel.Rewriter.CacheBreakpointsWritten));

            // Anthropic 把 max_tokens 当必填，OpenAI 的客户端常常不写。规则没补上就用默认值。
            if (AnthropicRequestPatcher.TryEnsureMaxTokens(body, DefaultMaxTokens, out byte[] patched))
            {
                body = patched;
            }

            (AnthropicThinkingStyle? fallback, string? thinkingType) = ThinkingStyleMemory.FallbackFor(ir, channel, style, upstreamModel);
            return new UpstreamPlan
            {
                Body = body,
                SwallowUsageOnlyFrames = false,
                ThinkingFallback = fallback,
                ThinkingType = thinkingType,
                Conversion = new ResponseConversion
                {
                    // 用请求 ID 当 id：换候选重试后客户端看到的还是同一个。
                    MessageId = "chatcmpl-" + requestId.ToString("N"),
                    FallbackModel = Model,
                    ClientWantsUsage = _clientWantsUsage,
                },
            };
        }
    }
}
