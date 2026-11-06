using System.Buffers;
using System.Text;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>
/// 把 IR 流事件写成 OpenAI Responses 的 SSE（ADR-0015）。每帧带 <c>event:</c> 行和 <c>sequence_number</c>，
/// 不发 <c>[DONE]</c>（Q195）。连续的文字 / 拒绝块合进同一个 message 项，项的 id 和
/// <see cref="ResponsesResponseWriter"/> 在终止事件里写的完整响应对得上。
/// </summary>
public sealed class ResponsesStreamEventWriter : IIrStreamEventWriter, IDisposable
{
    private readonly string _responseId;
    private readonly string _suffix;
    private readonly string _fallbackModel;
    private readonly long _createdAt;
    private readonly ArrayBufferWriter<byte> _json = new(512);
    private readonly Utf8JsonWriter _writer;
    private readonly Dictionary<int, Block> _blocks = [];
    private readonly List<IrBlock> _content = [];

    private string? _model;
    private IrStopReason? _stopReason;
    private Message? _message;
    private int _sequenceNumber;
    private int _nextItem;

    /// <param name="responseId">回给客户端的 <c>resp_…</c> id。</param>
    /// <param name="fallbackModel">上游没给模型名时用它。</param>
    /// <param name="createdAt">Unix 秒。</param>
    public ResponsesStreamEventWriter(string responseId, string fallbackModel, long createdAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(responseId);
        _responseId = responseId;
        _suffix = responseId.StartsWith("resp_", StringComparison.Ordinal) ? responseId[5..] : responseId;
        _fallbackModel = fallbackModel;
        _createdAt = createdAt;
        _writer = new Utf8JsonWriter(_json, ResponsesWire.WriterOptions);
    }

    /// <summary>已经写出 <c>response.created</c>。</summary>
    public bool Started { get; private set; }

    /// <summary>已经写出终止事件。</summary>
    public bool Completed { get; private set; }

    public void Write(IrStreamEvent item, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        if (item is IrStreamEvent.MessageStarted started)
        {
            _model ??= started.Model;
        }

        EnsureStarted(output);
        switch (item)
        {
            case IrStreamEvent.BlockStarted start:
                StartBlock(start, output);
                return;

            case IrStreamEvent.BlockDelta delta when delta.Text.Length > 0:
                WriteDelta(delta, output);
                return;

            case IrStreamEvent.BlockStopped stop:
                StopBlock(stop.Index, output);
                return;

            case IrStreamEvent.MessageEnded ended:
                _stopReason ??= ended.StopReason;
                return;

            default:
                return;
        }
    }

    /// <summary>关掉还开着的项，写 <c>response.completed</c>（截断时 <c>response.incomplete</c>），带完整响应和用量。</summary>
    public void Complete(Usage usage, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        EnsureStarted(output);
        foreach (int index in _blocks.Where(pair => !pair.Value.Stopped).Select(pair => pair.Key).ToArray())
        {
            StopBlock(index, output);
        }

        CloseMessage(output);

        IrStopReason? stop = _stopReason ?? (_content.Exists(block => block is IrToolCall) ? IrStopReason.ToolUse : IrStopReason.EndTurn);
        (string status, _) = ResponsesWire.Status(stop);
        string type = status == "incomplete" ? "response.incomplete" : "response.completed";

        Utf8JsonWriter w = Begin(type);
        w.WritePropertyName("response");
        ResponsesResponseWriter.WriteObject(w, _responseId, _model ?? _fallbackModel, _createdAt, _content, stop, usage);
        Emit(type, output);
        Completed = true;
    }

    public void WriteError(IBufferWriter<byte> output, string message)
    {
        ArgumentNullException.ThrowIfNull(output);
        Utf8JsonWriter w = Begin("error");
        w.WriteString("code", "server_error");
        w.WriteString("message", message);
        w.WriteNull("param");
        Emit("error", output);
    }

    /// <summary>Responses 风格的流内错误事件，序号从 0 起。给方言在没有写出器时用。</summary>
    public static void WriteError(IBufferWriter<byte> output, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(output);
        var json = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(json, ResponsesWire.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("type", "error");
            w.WriteNumber("sequence_number", 0);
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteNull("param");
            w.WriteEndObject();
        }

        output.Write("event: error\ndata: "u8);
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
        foreach (string type in (ReadOnlySpan<string>)["response.created", "response.in_progress"])
        {
            Utf8JsonWriter w = Begin(type);
            w.WriteStartObject("response");
            w.WriteString("id", _responseId);
            w.WriteString("object", "response");
            w.WriteNumber("created_at", _createdAt);
            w.WriteString("status", "in_progress");
            w.WriteNull("error");
            w.WriteNull("incomplete_details");
            w.WriteString("model", _model ?? _fallbackModel);
            w.WriteStartArray("output");
            w.WriteEndArray();
            w.WriteNull("usage");
            w.WriteEndObject();
            Emit(type, output);
        }
    }

    private void StartBlock(IrStreamEvent.BlockStarted start, IBufferWriter<byte> output)
    {
        if (_blocks.ContainsKey(start.Index))
        {
            return;
        }

        switch (start.Kind)
        {
            case IrStreamBlockKind.Text or IrStreamBlockKind.Refusal:
                {
                    Message message = OpenMessage(output);
                    var block = new Block(start.Kind, message.ItemId, message.OutputIndex) { ContentIndex = message.Parts++ };
                    _blocks[start.Index] = block;

                    Utf8JsonWriter w = Begin("response.content_part.added");
                    WriteLocation(w, block, withContentIndex: true);
                    w.WritePropertyName("part");
                    WritePart(w, block);
                    Emit("response.content_part.added", output);
                    return;
                }

            case IrStreamBlockKind.Reasoning:
                {
                    CloseMessage(output);
                    var block = new Block(start.Kind, $"rs_{_suffix}_{_nextItem}", _nextItem);
                    _nextItem++;
                    _blocks[start.Index] = block;

                    Utf8JsonWriter w = Begin("response.output_item.added");
                    w.WriteNumber("output_index", block.OutputIndex);
                    w.WriteStartObject("item");
                    w.WriteString("id", block.ItemId);
                    w.WriteString("type", "reasoning");
                    w.WriteStartArray("summary");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    Emit("response.output_item.added", output);

                    w = Begin("response.reasoning_summary_part.added");
                    WriteLocation(w, block, withContentIndex: false);
                    w.WriteNumber("summary_index", 0);
                    w.WritePropertyName("part");
                    WritePart(w, block);
                    Emit("response.reasoning_summary_part.added", output);
                    return;
                }

            case IrStreamBlockKind.ToolCall:
                {
                    CloseMessage(output);
                    var block = new Block(start.Kind, $"fc_{_suffix}_{_nextItem}", _nextItem)
                    {
                        CallId = start.ToolCallId is { Length: > 0 } id ? id : $"call_{_suffix}_{_nextItem}",
                        Name = start.ToolName ?? string.Empty,
                    };
                    _nextItem++;
                    _blocks[start.Index] = block;

                    Utf8JsonWriter w = Begin("response.output_item.added");
                    w.WriteNumber("output_index", block.OutputIndex);
                    w.WritePropertyName("item");
                    WriteFunctionCall(w, block, "in_progress");
                    Emit("response.output_item.added", output);
                    return;
                }

            default:
                // 遮蔽的推理 Responses 写不出来，整块跳过，也不打断正在写的 message。
                return;
        }
    }

    private void WriteDelta(IrStreamEvent.BlockDelta delta, IBufferWriter<byte> output)
    {
        if (!_blocks.TryGetValue(delta.Index, out Block? block) || block.Stopped)
        {
            return;
        }

        block.Text.Append(delta.Text);
        string type = block.Kind switch
        {
            IrStreamBlockKind.Text => "response.output_text.delta",
            IrStreamBlockKind.Refusal => "response.refusal.delta",
            IrStreamBlockKind.Reasoning => "response.reasoning_summary_text.delta",
            _ => "response.function_call_arguments.delta",
        };

        Utf8JsonWriter w = Begin(type);
        WriteLocation(w, block, withContentIndex: block.Kind is IrStreamBlockKind.Text or IrStreamBlockKind.Refusal);
        if (block.Kind == IrStreamBlockKind.Reasoning)
        {
            w.WriteNumber("summary_index", 0);
        }

        w.WriteString("delta", delta.Text);
        if (block.Kind == IrStreamBlockKind.Text)
        {
            w.WritePropertyName("logprobs");
            if (delta.LogprobsJson is not null)
            {
                w.WriteRawValue(delta.LogprobsJson, skipInputValidation: true);
            }
            else
            {
                w.WriteStartArray();
                w.WriteEndArray();
            }
        }

        Emit(type, output);
    }

    private void StopBlock(int index, IBufferWriter<byte> output)
    {
        if (!_blocks.TryGetValue(index, out Block? block) || block.Stopped)
        {
            return;
        }

        block.Stopped = true;
        string text = block.Text.ToString();
        switch (block.Kind)
        {
            case IrStreamBlockKind.Text or IrStreamBlockKind.Refusal:
                {
                    bool refusal = block.Kind == IrStreamBlockKind.Refusal;
                    string doneType = refusal ? "response.refusal.done" : "response.output_text.done";
                    Utf8JsonWriter w = Begin(doneType);
                    WriteLocation(w, block, withContentIndex: true);
                    w.WriteString(refusal ? "refusal" : "text", text);
                    if (!refusal)
                    {
                        w.WriteStartArray("logprobs");
                        w.WriteEndArray();
                    }

                    Emit(doneType, output);

                    w = Begin("response.content_part.done");
                    WriteLocation(w, block, withContentIndex: true);
                    w.WritePropertyName("part");
                    WritePart(w, block);
                    Emit("response.content_part.done", output);

                    var part = new IrText(text, refusal);
                    _message?.Content.Add(part);
                    _content.Add(part);
                    return;
                }

            case IrStreamBlockKind.Reasoning:
                {
                    Utf8JsonWriter w = Begin("response.reasoning_summary_text.done");
                    WriteLocation(w, block, withContentIndex: false);
                    w.WriteNumber("summary_index", 0);
                    w.WriteString("text", text);
                    Emit("response.reasoning_summary_text.done", output);

                    w = Begin("response.reasoning_summary_part.done");
                    WriteLocation(w, block, withContentIndex: false);
                    w.WriteNumber("summary_index", 0);
                    w.WritePropertyName("part");
                    WritePart(w, block);
                    Emit("response.reasoning_summary_part.done", output);

                    w = Begin("response.output_item.done");
                    w.WriteNumber("output_index", block.OutputIndex);
                    w.WriteStartObject("item");
                    w.WriteString("id", block.ItemId);
                    w.WriteString("type", "reasoning");
                    w.WriteStartArray("summary");
                    if (text.Length > 0)
                    {
                        WritePart(w, block);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                    Emit("response.output_item.done", output);

                    _content.Add(new IrReasoning(text, null));
                    return;
                }

            case IrStreamBlockKind.ToolCall:
                {
                    Utf8JsonWriter w = Begin("response.function_call_arguments.done");
                    WriteLocation(w, block, withContentIndex: false);
                    w.WriteString("arguments", text);
                    Emit("response.function_call_arguments.done", output);

                    w = Begin("response.output_item.done");
                    w.WriteNumber("output_index", block.OutputIndex);
                    w.WritePropertyName("item");
                    WriteFunctionCall(w, block, "completed");
                    Emit("response.output_item.done", output);

                    _content.Add(new IrToolCall(block.CallId!, block.Name!, text.Length > 0 ? text : "{}"));
                    return;
                }
        }
    }

    private Message OpenMessage(IBufferWriter<byte> output)
    {
        if (_message is { } open)
        {
            return open;
        }

        var message = new Message($"msg_{_suffix}_{_nextItem}", _nextItem);
        _nextItem++;
        _message = message;

        Utf8JsonWriter w = Begin("response.output_item.added");
        w.WriteNumber("output_index", message.OutputIndex);
        w.WriteStartObject("item");
        w.WriteString("id", message.ItemId);
        w.WriteString("type", "message");
        w.WriteString("role", "assistant");
        w.WriteString("status", "in_progress");
        w.WriteStartArray("content");
        w.WriteEndArray();
        w.WriteEndObject();
        Emit("response.output_item.added", output);
        return message;
    }

    private void CloseMessage(IBufferWriter<byte> output)
    {
        if (_message is not { } message)
        {
            return;
        }

        // 先把这个 message 名下还开着的块关掉，内容才完整。
        foreach (KeyValuePair<int, Block> pair in _blocks.Where(p => !p.Value.Stopped && p.Value.ItemId == message.ItemId).ToArray())
        {
            StopBlock(pair.Key, output);
        }

        _message = null;
        Utf8JsonWriter w = Begin("response.output_item.done");
        w.WriteNumber("output_index", message.OutputIndex);
        w.WriteStartObject("item");
        w.WriteString("id", message.ItemId);
        w.WriteString("type", "message");
        w.WriteString("role", "assistant");
        w.WriteString("status", "completed");
        w.WriteStartArray("content");
        foreach (IrText part in message.Content)
        {
            ResponsesResponseWriter.WritePart(w, part);
        }

        w.WriteEndArray();
        w.WriteEndObject();
        Emit("response.output_item.done", output);
    }

    private static void WriteLocation(Utf8JsonWriter w, Block block, bool withContentIndex)
    {
        w.WriteString("item_id", block.ItemId);
        w.WriteNumber("output_index", block.OutputIndex);
        if (withContentIndex)
        {
            w.WriteNumber("content_index", block.ContentIndex);
        }
    }

    private static void WritePart(Utf8JsonWriter w, Block block)
    {
        string text = block.Text.ToString();
        w.WriteStartObject();
        switch (block.Kind)
        {
            case IrStreamBlockKind.Refusal:
                w.WriteString("type", "refusal");
                w.WriteString("refusal", text);
                break;
            case IrStreamBlockKind.Reasoning:
                w.WriteString("type", "summary_text");
                w.WriteString("text", text);
                break;
            default:
                w.WriteString("type", "output_text");
                w.WriteString("text", text);
                w.WriteStartArray("annotations");
                w.WriteEndArray();
                break;
        }

        w.WriteEndObject();
    }

    private static void WriteFunctionCall(Utf8JsonWriter w, Block block, string status)
    {
        w.WriteStartObject();
        w.WriteString("id", block.ItemId);
        w.WriteString("type", "function_call");
        w.WriteString("status", status);
        w.WriteString("call_id", block.CallId);
        w.WriteString("name", block.Name);
        w.WriteString("arguments", block.Text.ToString());
        w.WriteEndObject();
    }

    /// <summary>开一个事件对象，先写 <c>type</c> 和 <c>sequence_number</c>，调用方接着写字段。</summary>
    private Utf8JsonWriter Begin(string type)
    {
        _json.ResetWrittenCount();
        _writer.Reset(_json);
        _writer.WriteStartObject();
        _writer.WriteString("type", type);
        _writer.WriteNumber("sequence_number", _sequenceNumber++);
        return _writer;
    }

    private void Emit(string type, IBufferWriter<byte> output)
    {
        _writer.WriteEndObject();
        _writer.Flush();
        output.Write("event: "u8);
        output.Write(Encoding.UTF8.GetBytes(type));
        output.Write("\ndata: "u8);
        output.Write(_json.WrittenSpan);
        output.Write("\n\n"u8);
    }

    private sealed class Block(IrStreamBlockKind kind, string itemId, int outputIndex)
    {
        public IrStreamBlockKind Kind { get; } = kind;

        public string ItemId { get; } = itemId;

        public int OutputIndex { get; } = outputIndex;

        public int ContentIndex { get; init; }

        public string? CallId { get; init; }

        public string? Name { get; init; }

        public StringBuilder Text { get; } = new();

        public bool Stopped { get; set; }
    }

    private sealed class Message(string itemId, int outputIndex)
    {
        public string ItemId { get; } = itemId;

        public int OutputIndex { get; } = outputIndex;

        public int Parts { get; set; }

        public List<IrText> Content { get; } = [];
    }
}
