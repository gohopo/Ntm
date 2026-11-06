using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Forwarding;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Relay;

/// <summary>
/// Anthropic Messages 入站。Anthropic 上游同格式透传，只换 model（Q164）；
/// OpenAI Chat 与 Responses 上游经 IR 转换（ADR-0001、ADR-0005）。
/// </summary>
internal sealed class AnthropicDialect : InboundDialect
{
    // 响应是 application/json 或 SSE，不会嵌进 HTML，不需要把中文转义成 \uXXXX。
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public override ApiFormat Format => ApiFormat.Anthropic;

    // 透传到 Anthropic 上游时，上游的 400 原样还给客户端；转换时中继会改为重新包装。
    public override bool EchoesUpstreamClientErrors => true;

    public override InboundParseResult Parse(byte[] body)
    {
        AnthropicRequestReading reading = AnthropicRequestReader.Read(body);
        if (!reading.IsValid)
        {
            return new InboundParseResult(null, reading.Error ?? "请求结构不符合 Anthropic Messages 格式。");
        }

        IrRequest ir = reading.Request!;
        return string.IsNullOrEmpty(ir.Model)
            ? new InboundParseResult(null, "请求里缺少 model。")
            : new InboundParseResult(new Request(ir, body, reading.AnthropicOnly), null);
    }

    public override byte[] ErrorBody(GatewayErrorKind kind, string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteError(writer, kind, message);
        }

        return buffer.ToArray();
    }

    public override byte[] ErrorFrame(GatewayErrorKind kind, string message)
    {
        using var buffer = new MemoryStream();
        buffer.Write("event: error\ndata: "u8);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteError(writer, kind, message);
        }

        buffer.Write("\n\n"u8);
        return buffer.ToArray();
    }

    private static void WriteError(Utf8JsonWriter writer, GatewayErrorKind kind, string message)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "error");
        writer.WriteStartObject("error");
        writer.WriteString("type", ErrorType(kind));
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // Anthropic 的错误类型表：认证、权限、计费、请求过大、请求无效、限流各有专名，其余都是 api_error。
    private static string ErrorType(GatewayErrorKind kind) => kind switch
    {
        GatewayErrorKind.MissingApiKey or GatewayErrorKind.InvalidApiKey => "authentication_error",
        GatewayErrorKind.BodyTooLarge => "request_too_large",
        GatewayErrorKind.InvalidRequest => "invalid_request_error",
        GatewayErrorKind.ModelNotFound => "not_found_error",
        GatewayErrorKind.RateLimited => "rate_limit_error",
        GatewayErrorKind.AllCooling => "overloaded_error",

        // Anthropic 的 billing_error 本来配 402；这里状态码按 Q78 是 429，类型名取它的语义。
        GatewayErrorKind.QuotaExhausted => "billing_error",
        GatewayErrorKind.ModelNotAllowed => "permission_error",
        GatewayErrorKind.ResourceNotFound => "not_found_error",
        GatewayErrorKind.ChannelNotAllowed => "permission_error",

        // Anthropic 没有表示「被中断」的类型，取最接近的（Q116）。
        GatewayErrorKind.Interrupted => "permission_error",
        GatewayErrorKind.ShuttingDown => "overloaded_error",
        _ => "api_error",
    };

    private sealed class Request : InboundRequest
    {
        private readonly IrRequest _ir;
        private readonly byte[] _body;
        private readonly string? _anthropicOnly;
        private readonly Dictionary<string, UpstreamPlan> _plans = new(StringComparer.Ordinal);

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Request(IrRequest ir, byte[] body, string? anthropicOnly)
        {
            _ir = ir;
            _body = body;
            _anthropicOnly = anthropicOnly;
            Model = ir.Model;
            IsStream = ir.Stream;
        }

        public override bool CanBeServedBy(ApiFormat upstream, out string? reason)
        {
            reason = upstream switch
            {
                ApiFormat.Anthropic => null,

                // 服务端工具只有 Anthropic 自己执行得了（Q218）。
                _ when _anthropicOnly is not null => _anthropicOnly,
                ApiFormat.OpenAiChat => null,

                // Responses 没有停止序列，带着它的请求要绕开（Q185、ADR-0014）。
                ApiFormat.OpenAiResponses => IrTargetCheck.Unrepresentable(_ir, upstream),
                _ => "这个上游格式还不支持。",
            };
            return reason is null;
        }

        public override UpstreamPlan PlanFor(ChannelSnapshot channel, string upstreamModel, Guid requestId, Protocol.Anthropic.AnthropicThinkingStyle? thinkingStyle = null)
        {
            ArgumentNullException.ThrowIfNull(channel);

            // 同一个上游格式 + 模型的字节只算一次，换候选时直接重发；渠道有请求体规则时按渠道分开（Q149）。
            string cacheKey = channel.Rewriter.HasBodyRules
                ? channel.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + upstreamModel
                : (int)channel.ApiFormat + "\n" + upstreamModel;
            if (_plans.TryGetValue(cacheKey, out UpstreamPlan? cached))
            {
                return cached;
            }

            UpstreamPlan plan = channel.ApiFormat switch
            {
                ApiFormat.Anthropic => BuildPassthroughPlan(channel, upstreamModel),
                ApiFormat.OpenAiResponses => BuildResponsesPlan(channel, upstreamModel, requestId),
                _ => BuildConvertedPlan(channel, upstreamModel, requestId),
            };
            _plans[cacheKey] = plan;
            return plan;
        }

        /// <summary>Anthropic 上游：原始字节照搬，只把 model 换成上游模型名（Q164、ADR-0002）。</summary>
        private UpstreamPlan BuildPassthroughPlan(ChannelSnapshot channel, string upstreamModel)
        {
            byte[] source = channel.Rewriter.ApplyToBody(_body);
            byte[] body = OpenAiChatRequestPatcher.TryReplaceModel(source, upstreamModel, out byte[] renamed)
                ? renamed
                : source;

            return new UpstreamPlan { Body = body };
        }

        /// <summary>Responses 上游：从 IR 写出 Responses 请求体（store:false，Q184），再按规则改写。</summary>
        private UpstreamPlan BuildResponsesPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId)
        {
            byte[] body = channel.Rewriter.ApplyToBody(ResponsesRequestWriter.Write(_ir, upstreamModel, PromptCacheKey));
            return new UpstreamPlan
            {
                Body = body,
                Conversion = new ResponseConversion
                {
                    MessageId = "msg_" + requestId.ToString("N"),
                    FallbackModel = Model,
                },
            };
        }

        private UpstreamPlan BuildConvertedPlan(ChannelSnapshot channel, string upstreamModel, Guid requestId)
        {
            // 规则作用在转换后、真正发往上游的那份请求上，路径按上游格式写（Q150）。
            // 转换器写出来的 model 和 stream 已经定好，规则改不到它们（Q145）。
            byte[] body = channel.Rewriter.ApplyToBody(OpenAiChatRequestWriter.Write(_ir, upstreamModel));

            return new UpstreamPlan
            {
                Body = body,

                // 转换路径自己写了 include_usage，用量帧不会翻译给客户端，不需要再剔。
                SwallowUsageOnlyFrames = false,
                Conversion = new ResponseConversion
                {
                    // 用请求 ID 当消息 ID：换候选重试后客户端看到的还是同一个 ID。
                    MessageId = "msg_" + requestId.ToString("N"),
                    FallbackModel = Model,
                },
            };
        }
    }
}
