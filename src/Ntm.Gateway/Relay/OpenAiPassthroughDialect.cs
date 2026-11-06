using Ntm.Gateway.Configuration;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 嵌入和图片生成的入站（Q220–Q227）。只转给 OpenAI Chat 格式的渠道，请求体原样透传，
/// 只把 <c>model</c> 换成上游模型名；不走 IR，不套请求体改写规则（那是为对话写的，Q226）。
/// 网关只要求请求体是 JSON 对象、<c>model</c> 是非空字符串，其余字段不校验也不改（Q227）。
/// </summary>
internal sealed class OpenAiPassthroughDialect(RequestKind kind) : InboundDialect
{
    public override ApiFormat Format => ApiFormat.OpenAiChat;

    public override RequestKind Kind => kind;

    public override bool EchoesUpstreamClientErrors => true;

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

        // 嵌入没有流式，客户端写了 stream 也按非流式处理（Q232）。
        return new InboundParseResult(new Request(body, reading.Model, kind == RequestKind.Image && reading.IsStream), null);
    }

    public override byte[] ErrorBody(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = OpenAiChatDialect.Describe(kind);
        return OpenAiErrorWriter.Body(message, type, code);
    }

    public override byte[] ErrorFrame(GatewayErrorKind kind, string message)
    {
        (string type, string? code) = OpenAiChatDialect.Describe(kind);
        return OpenAiErrorWriter.StreamFrame(message, type, code);
    }

    private sealed class Request : InboundRequest
    {
        private readonly byte[] _body;
        private readonly Dictionary<string, UpstreamPlan> _plans = [];

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Request(byte[] body, string model, bool isStream)
        {
            _body = body;
            Model = model;
            IsStream = isStream;
        }

        public override bool CanBeServedBy(ApiFormat upstream, out string? reason)
        {
            reason = null;
            return upstream == ApiFormat.OpenAiChat;
        }

        public override UpstreamPlan PlanFor(ChannelSnapshot channel, string upstreamModel, Guid requestId, Protocol.Anthropic.AnthropicThinkingStyle? thinkingStyle = null)
        {
            ArgumentNullException.ThrowIfNull(upstreamModel);

            if (_plans.TryGetValue(upstreamModel, out UpstreamPlan? cached))
            {
                return cached;
            }

            byte[] body = OpenAiChatRequestPatcher.TryReplaceModel(_body, upstreamModel, out byte[] renamed)
                ? renamed
                : _body;
            var plan = new UpstreamPlan { Body = body };
            _plans[upstreamModel] = plan;
            return plan;
        }
    }
}
