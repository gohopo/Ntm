using System.Buffers;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// OpenAI Chat 的流式块 → Anthropic Messages 的 SSE。
/// 由 <see cref="OpenAiChatStreamEventReader"/> 和 <see cref="AnthropicStreamEventWriter"/> 拼成（ADR-0015）。
/// </summary>
public sealed class OpenAiChatToAnthropicStreamTranslator : IDisposable
{
    private readonly AnthropicStreamEventWriter _writer;
    private readonly IrStreamTranslator _translator;

    /// <param name="messageId">完整的消息 id（含 <c>msg_</c> 前缀）。</param>
    /// <param name="fallbackModel">上游块里没有模型名时用它。</param>
    public OpenAiChatToAnthropicStreamTranslator(string messageId, string fallbackModel)
    {
        _writer = new AnthropicStreamEventWriter(messageId, fallbackModel);
        _translator = new IrStreamTranslator(new OpenAiChatStreamEventReader(), _writer);
    }

    /// <summary>已经写出 <c>message_start</c>。</summary>
    public bool Started => _writer.Started;

    /// <summary>已经写出 <c>message_stop</c>。</summary>
    public bool Completed => _writer.Completed;

    public void Translate(ReadOnlySpan<byte> data, IBufferWriter<byte> output) => _translator.Translate(data, output);

    public void Complete(Usage usage, IBufferWriter<byte> output) => _translator.Complete(usage, output);

    /// <summary>Anthropic 的流内错误事件。提交之后上游失败时只能这样告诉客户端。</summary>
    public static void WriteError(IBufferWriter<byte> output, string type, string message) =>
        AnthropicStreamEventWriter.WriteError(output, type, message);

    public void Dispose() => _translator.Dispose();
}
