using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 把 IR 流事件写成 OpenAI Chat 的 <c>chat.completion.chunk</c>（ADR-0015）。
/// 工具调用的 <c>index</c> 由这里按出现顺序自己编，和块序号无关。
/// 用量块只在客户端要了（<c>stream_options.include_usage</c>）时才写，最后以 <c>[DONE]</c> 结束。
/// </summary>
public sealed class OpenAiChatStreamEventWriter : IIrStreamEventWriter, IDisposable
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _id;
    private readonly string _fallbackModel;
    private readonly long _created;
    private readonly bool _includeUsage;
    private readonly ArrayBufferWriter<byte> _json = new(512);
    private readonly Utf8JsonWriter _writer;
    private readonly Dictionary<int, int> _toolIndexByBlock = [];

    private string? _model;
    private bool _sawFinish;
    private int _nextToolIndex;

    /// <param name="id">回给客户端的 <c>chatcmpl-…</c> id。</param>
    /// <param name="fallbackModel">上游没给模型名时用它。</param>
    /// <param name="created">Unix 秒。</param>
    /// <param name="includeUsage">客户端要了用量块。</param>
    public OpenAiChatStreamEventWriter(string id, string fallbackModel, long created, bool includeUsage)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _id = id;
        _fallbackModel = fallbackModel;
        _created = created;
        _includeUsage = includeUsage;
        _writer = new Utf8JsonWriter(_json, WriterOptions);
    }

    /// <summary>已经写出带 role 的第一块。</summary>
    public bool Started { get; private set; }

    /// <summary>已经写出 <c>[DONE]</c>。</summary>
    public bool Completed { get; private set; }

    public void Write(IrStreamEvent item, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        switch (item)
        {
            case IrStreamEvent.MessageStarted started:
                _model ??= started.Model;
                EnsureStarted(output);
                return;

            case IrStreamEvent.BlockStarted start:
                EnsureStarted(output);
                WriteBlockStart(output, start);
                return;

            case IrStreamEvent.BlockDelta delta when delta.Text.Length > 0:
                EnsureStarted(output);
                WriteBlockDelta(output, delta);
                return;

            case IrStreamEvent.MessageEnded ended when !_sawFinish:
                EnsureStarted(output);
                WriteFinish(output, OpenAiChatResponseWriter.FinishReason(ended.StopReason, _nextToolIndex > 0));
                return;

            default:
                return;
        }
    }

    /// <summary>补上没写的结束块，要用量时写用量块，最后写 <c>[DONE]</c>。</summary>
    public void Complete(Usage usage, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        EnsureStarted(output);
        if (!_sawFinish)
        {
            WriteFinish(output, OpenAiChatResponseWriter.FinishReason(null, _nextToolIndex > 0));
        }

        if (_includeUsage && usage.HasValue)
        {
            Utf8JsonWriter w = Begin();
            w.WriteStartObject();
            WriteHead(w);
            w.WriteStartArray("choices");
            w.WriteEndArray();
            OpenAiChatResponseWriter.WriteUsage(w, usage);
            w.WriteEndObject();
            Emit(output);
        }

        output.Write("data: [DONE]\n\n"u8);
        Completed = true;
    }

    public void WriteError(IBufferWriter<byte> output, string message) => WriteError(output, "server_error", message);

    /// <summary>OpenAI 风格的流内错误块。提交之后上游失败时只能这样告诉客户端。</summary>
    public static void WriteError(IBufferWriter<byte> output, string type, string message)
    {
        ArgumentNullException.ThrowIfNull(output);

        var json = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(json, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("type", type);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        output.Write("data: "u8);
        output.Write(json.WrittenSpan);
        output.Write("\n\n"u8);
    }

    public void Dispose() => _writer.Dispose();

    private void EnsureStarted(IBufferWriter<byte> output)
    {
        if (Started)
        {
            return;
        }

        Started = true;
        Utf8JsonWriter w = BeginChoice();
        w.WriteString("role", "assistant");
        w.WriteString("content", string.Empty);
        EndChoice(output);
    }

    private void WriteBlockStart(IBufferWriter<byte> output, IrStreamEvent.BlockStarted start)
    {
        switch (start.Kind)
        {
            case IrStreamBlockKind.ToolCall when !_toolIndexByBlock.ContainsKey(start.Index):
                {
                    int toolIndex = _nextToolIndex++;
                    _toolIndexByBlock[start.Index] = toolIndex;

                    Utf8JsonWriter w = BeginChoice();
                    w.WriteStartArray("tool_calls");
                    w.WriteStartObject();
                    w.WriteNumber("index", toolIndex);
                    w.WriteString("id", start.ToolCallId is { Length: > 0 } id ? id : $"call_{_id}_{toolIndex}");
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", start.ToolName ?? string.Empty);
                    w.WriteString("arguments", string.Empty);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndArray();
                    EndChoice(output);
                    return;
                }

            case IrStreamBlockKind.RedactedReasoning:
                {
                    // OpenAI Chat 没有地方放加密的思考内容，但这是真实输出，
                    // 给一个空的推理增量，让客户端知道模型在想（内容本身丢掉）。
                    Utf8JsonWriter w = BeginChoice();
                    w.WriteString("reasoning_content", string.Empty);
                    EndChoice(output);
                    return;
                }

            default:
                return;
        }
    }

    private void WriteBlockDelta(IBufferWriter<byte> output, IrStreamEvent.BlockDelta delta)
    {
        if (delta.Kind == IrStreamBlockKind.ToolCall)
        {
            if (!_toolIndexByBlock.TryGetValue(delta.Index, out int toolIndex))
            {
                return;
            }

            Utf8JsonWriter t = BeginChoice();
            t.WriteStartArray("tool_calls");
            t.WriteStartObject();
            t.WriteNumber("index", toolIndex);
            t.WriteStartObject("function");
            t.WriteString("arguments", delta.Text);
            t.WriteEndObject();
            t.WriteEndObject();
            t.WriteEndArray();
            EndChoice(output);
            return;
        }

        string? field = delta.Kind switch
        {
            IrStreamBlockKind.Text => "content",
            IrStreamBlockKind.Reasoning => "reasoning_content",
            IrStreamBlockKind.Refusal => "refusal",
            _ => null,
        };

        if (field is null)
        {
            return;
        }

        Utf8JsonWriter w = BeginChoice();
        w.WriteString(field, delta.Text);
        EndChoice(output, delta.Kind == IrStreamBlockKind.Text ? delta.LogprobsJson : null);
    }

    private void WriteFinish(IBufferWriter<byte> output, string finishReason)
    {
        _sawFinish = true;
        Utf8JsonWriter w = BeginChoice();
        w.WriteEndObject();
        w.WriteString("finish_reason", finishReason);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        Emit(output);
    }

    /// <summary>写到 <c>choices[0].delta</c> 里面，调用方接着写 delta 的字段。</summary>
    private Utf8JsonWriter BeginChoice()
    {
        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        WriteHead(w);
        w.WriteStartArray("choices");
        w.WriteStartObject();
        w.WriteNumber("index", 0);
        w.WriteStartObject("delta");
        return w;
    }

    private void EndChoice(IBufferWriter<byte> output, string? logprobsJson = null)
    {
        Utf8JsonWriter w = _writer;
        w.WriteEndObject();
        if (logprobsJson is not null)
        {
            w.WriteStartObject("logprobs");
            w.WritePropertyName("content");
            w.WriteRawValue(logprobsJson, skipInputValidation: true);
            w.WriteEndObject();
        }

        w.WriteNull("finish_reason");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        Emit(output);
    }

    private void WriteHead(Utf8JsonWriter w)
    {
        w.WriteString("id", _id);
        w.WriteString("object", "chat.completion.chunk");
        w.WriteNumber("created", _created);
        w.WriteString("model", _model ?? _fallbackModel);
    }

    private Utf8JsonWriter Begin()
    {
        _json.ResetWrittenCount();
        _writer.Reset(_json);
        return _writer;
    }

    private void Emit(IBufferWriter<byte> output)
    {
        _writer.Flush();
        output.Write("data: "u8);
        output.Write(_json.WrittenSpan);
        output.Write("\n\n"u8);
    }
}
