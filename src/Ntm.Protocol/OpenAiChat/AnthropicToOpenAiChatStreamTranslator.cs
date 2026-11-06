using System.Buffers;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// Anthropic Messages 的 SSE → OpenAI Chat 的流式块。
/// 由 <see cref="AnthropicStreamEventReader"/> 和 <see cref="OpenAiChatStreamEventWriter"/> 拼成（ADR-0015）。
/// </summary>
public sealed class AnthropicToOpenAiChatStreamTranslator : IDisposable
{
    private readonly OpenAiChatStreamEventWriter _writer;
    private readonly IrStreamTranslator _translator;

    public AnthropicToOpenAiChatStreamTranslator(string id, string fallbackModel, long created, bool includeUsage)
    {
        _writer = new OpenAiChatStreamEventWriter(id, fallbackModel, created, includeUsage);
        _translator = new IrStreamTranslator(new AnthropicStreamEventReader(), _writer);
    }

    public bool Started => _writer.Started;

    public bool Completed => _writer.Completed;

    public void Translate(ReadOnlySpan<byte> data, IBufferWriter<byte> output) => _translator.Translate(data, output);

    public void Complete(Usage usage, IBufferWriter<byte> output) => _translator.Complete(usage, output);

    /// <summary>OpenAI 风格的流内错误块。</summary>
    public static void WriteError(IBufferWriter<byte> output, string type, string message) =>
        OpenAiChatStreamEventWriter.WriteError(output, type, message);

    public void Dispose() => _translator.Dispose();
}
