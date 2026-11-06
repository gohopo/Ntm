using System.Buffers;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 按上游格式挑检视器，把三种格式的流检视收成同一个面。
/// 判定仍然只来自检视器：转发器不自己解析字节（ADR-0002）。
/// </summary>
internal sealed class UpstreamStreamInspection
{
    private readonly OpenAiChatStreamInspector? _openAi;
    private readonly AnthropicStreamInspector? _anthropic;
    private readonly ResponsesStreamInspector? _responses;
    private readonly OpenAiImageStreamInspector? _image;

    public UpstreamStreamInspection(ApiFormat upstreamFormat, RequestKind kind = RequestKind.Chat)
    {
        if (kind == RequestKind.Image)
        {
            // 图片流只在 OpenAI 格式之间透传（Q221/Q232）。
            _image = new OpenAiImageStreamInspector();
            return;
        }

        switch (upstreamFormat)
        {
            case ApiFormat.Anthropic:
                _anthropic = new AnthropicStreamInspector();
                break;
            case ApiFormat.OpenAiResponses:
                _responses = new ResponsesStreamInspector();
                break;
            default:
                _openAi = new OpenAiChatStreamInspector();
                break;
        }
    }

    public bool HasContentEvent =>
        _image is not null ? _image.HasContentEvent :
        _anthropic is not null ? _anthropic.HasContentEvent
        : _responses is not null ? _responses.HasContentEvent
        : _openAi!.HasContentEvent;

    public bool SawTerminal =>
        _image is not null ? _image.SawTerminal :
        _anthropic is not null ? _anthropic.SawTerminal
        : _responses is not null ? _responses.SawTerminal
        : _openAi!.SawTerminal;

    public Usage Usage =>
        _image is not null ? _image.Usage :
        _anthropic is not null ? _anthropic.Usage
        : _responses is not null ? _responses.Usage
        : _openAi!.Usage;

    /// <summary>只有输入侧的用量，Anthropic 流中途断掉时用（Q177）。另两种格式没有这一步。</summary>
    public Usage PartialUsage => _anthropic is not null ? _anthropic.InputUsage : Usage.Unavailable;

    public string? FinishReason =>
        _image is not null ? null :
        _anthropic is not null ? _anthropic.FinishReason
        : _responses is not null ? _responses.FinishReason
        : _openAi!.FinishReason;

    public string? ErrorPayload =>
        _image is not null ? _image.ErrorPayload ?? (_image.ImageCount == 0 ? _image.PartialFailure : null) :
        _anthropic is not null ? _anthropic.ErrorPayload
        : _responses is not null ? _responses.ErrorPayload
        : _openAi!.ErrorPayload;

    public string? UpstreamModel =>
        _image is not null ? _image.UpstreamModel :
        _anthropic is not null ? _anthropic.UpstreamModel
        : _responses is not null ? _responses.UpstreamModel
        : _openAi!.UpstreamModel;

    /// <summary>图片流生成的张数（Q230）。别的请求是 0。</summary>
    public int ImageCount => _image?.ImageCount ?? 0;

    /// <summary>Responses 上游给的响应 id，记下来给后面的 previous_response_id 找落点（ADR-0016）。</summary>
    public string? ResponseId => _responses?.ResponseId;

    public long OutputAsciiChars =>
        _image is not null ? 0 :
        _anthropic is not null ? _anthropic.OutputAsciiChars
        : _responses is not null ? _responses.OutputAsciiChars
        : _openAi!.OutputAsciiChars;

    public long OutputWideChars =>
        _image is not null ? 0 :
        _anthropic is not null ? _anthropic.OutputWideChars
        : _responses is not null ? _responses.OutputWideChars
        : _openAi!.OutputWideChars;

    public bool SawThinking =>
        _image is not null ? false :
        _anthropic is not null ? _anthropic.SawThinking
        : _responses is not null ? _responses.SawThinking
        : _openAi!.SawThinking;

    public bool EndedEmpty =>
        _image is not null ? _image.EndedEmpty :
        _anthropic is not null ? _anthropic.EndedEmpty
        : _responses is not null ? _responses.EndedEmpty
        : _openAi!.EndedEmpty;

    public StreamFrameVerdict Inspect(ReadOnlySpan<byte> data) =>
        _image is not null ? _image.Inspect(data) :
        _anthropic is not null ? _anthropic.Inspect(data)
        : _responses is not null ? _responses.Inspect(data)
        : _openAi!.Inspect(data);
}

/// <summary>
/// 流式转换：读上游格式成事件，再写成客户端格式（ADR-0015）。读取器按上游格式挑，
/// 写出器按入站格式挑，任意两种格式之间都走同一条路。翻译器只产出字节，不参与判定。
/// </summary>
internal sealed class UpstreamStreamTranslation : IDisposable
{
    private readonly IrStreamTranslator _translator;

    private UpstreamStreamTranslation(IrStreamTranslator translator) => _translator = translator;

    public static UpstreamStreamTranslation For(ForwardRequest request, long created)
    {
        ResponseConversion conversion = request.Conversion!;
        IIrStreamEventReader reader = request.UpstreamApiFormat switch
        {
            ApiFormat.Anthropic => new AnthropicStreamEventReader(),
            ApiFormat.OpenAiResponses => new ResponsesStreamEventReader(),
            _ => new OpenAiChatStreamEventReader(),
        };

        IIrStreamEventWriter writer = request.InboundApiFormat switch
        {
            ApiFormat.Anthropic => new AnthropicStreamEventWriter(conversion.MessageId, conversion.FallbackModel),
            ApiFormat.OpenAiResponses => new ResponsesStreamEventWriter(conversion.MessageId, conversion.FallbackModel, created),
            _ => new OpenAiChatStreamEventWriter(conversion.MessageId, conversion.FallbackModel, created, conversion.ClientWantsUsage),
        };

        return new UpstreamStreamTranslation(new IrStreamTranslator(reader, writer));
    }

    public void Translate(ReadOnlySpan<byte> data, IBufferWriter<byte> output) => _translator.Translate(data, output);

    public void Complete(Usage usage, IBufferWriter<byte> output) => _translator.Complete(usage, output);

    /// <summary>已经提交之后上游失败：按客户端格式补一个流内错误帧（ADR-0008）。</summary>
    public void WriteError(IBufferWriter<byte> output, string message) => _translator.WriteError(output, message);

    public void Dispose() => _translator.Dispose();
}

/// <summary>按上游格式读一次非流式响应，收成同一个面。</summary>
internal readonly record struct UpstreamResponseReading
{
    public bool IsError { get; init; }

    public bool IsMalformed { get; init; }

    public bool SawThinking { get; init; }

    public Usage Usage { get; init; }

    public string? FinishReason { get; init; }

    public string? ErrorPayload { get; init; }

    public string? UpstreamModel { get; init; }

    /// <summary>Responses 上游给的响应 id（ADR-0016）。</summary>
    public string? ResponseId { get; init; }

    /// <summary>读成 IR 的响应，供转换用；只在需要转换时才算。</summary>
    public IrResponse? Response { get; init; }

    public static UpstreamResponseReading Read(ReadOnlySpan<byte> body, ApiFormat upstreamFormat, bool needsIr)
    {
        if (upstreamFormat == ApiFormat.Anthropic)
        {
            AnthropicResponseReading reading = AnthropicResponseInspector.Read(body);
            return new UpstreamResponseReading
            {
                IsError = reading.IsError,
                IsMalformed = reading.IsMalformed,
                SawThinking = reading.SawThinking,
                Usage = reading.Usage,
                FinishReason = reading.FinishReason,
                ErrorPayload = reading.ErrorPayload,
                UpstreamModel = reading.UpstreamModel,
                Response = reading.Response,
            };
        }

        if (upstreamFormat == ApiFormat.OpenAiResponses)
        {
            ResponsesResponseReading reading = ResponsesResponseInspector.Read(body);
            return new UpstreamResponseReading
            {
                IsError = reading.IsError,
                IsMalformed = reading.IsMalformed,
                SawThinking = reading.SawThinking,
                Usage = reading.Usage,
                FinishReason = reading.FinishReason,
                ErrorPayload = reading.ErrorPayload,
                UpstreamModel = reading.UpstreamModel,
                ResponseId = reading.ResponseId,
                Response = reading.Response,
            };
        }

        OpenAiChatResponseReading openAi = OpenAiChatResponseInspector.Read(body);
        return new UpstreamResponseReading
        {
            IsError = openAi.IsError,
            IsMalformed = openAi.IsMalformed,
            SawThinking = openAi.SawThinking,
            Usage = openAi.Usage,
            FinishReason = openAi.FinishReason,
            ErrorPayload = openAi.ErrorPayload,
            UpstreamModel = openAi.UpstreamModel,
            Response = needsIr && !openAi.IsError && !openAi.IsMalformed
                ? OpenAiChatResponseConverter.ToIr(body)
                : null,
        };
    }
}
