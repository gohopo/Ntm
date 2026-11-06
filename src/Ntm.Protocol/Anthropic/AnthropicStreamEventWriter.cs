using System.Buffers;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 把 IR 流事件写成 Anthropic Messages 的 SSE（ADR-0015）。
/// 块序号由这里自己排：上游的编号可能有空洞，Anthropic 客户端按序号累积内容。
/// </summary>
public sealed class AnthropicStreamEventWriter : IIrStreamEventWriter, IDisposable
{
    private readonly string _messageId;
    private readonly string _toolIdPrefix;
    private readonly string _fallbackModel;
    private readonly ArrayBufferWriter<byte> _json = new(512);
    private readonly Utf8JsonWriter _writer;
    private readonly Dictionary<int, int> _blocks = [];
    private readonly List<int> _open = [];

    private string? _model;
    private IrStopReason? _stopReason;
    private bool _sawToolCall;
    private int _nextIndex;

    /// <param name="messageId">完整的消息 id（含 <c>msg_</c> 前缀）。</param>
    /// <param name="fallbackModel">上游没给模型名时用它。</param>
    public AnthropicStreamEventWriter(string messageId, string fallbackModel)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        _messageId = messageId;
        _toolIdPrefix = "toolu_" + (messageId.StartsWith("msg_", StringComparison.Ordinal) ? messageId[4..] : messageId) + "_";
        _fallbackModel = fallbackModel;
        _writer = new Utf8JsonWriter(_json, AnthropicWire.WriterOptions);
    }

    /// <summary>已经写出 <c>message_start</c>。</summary>
    public bool Started { get; private set; }

    /// <summary>已经写出 <c>message_stop</c>。</summary>
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
                EnsureStarted(output, started.InputUsage);
                return;

            case IrStreamEvent.BlockStarted start:
                EnsureStarted(output, Usage.Unavailable);
                WriteBlockStart(output, start);
                return;

            case IrStreamEvent.BlockDelta delta:
                EnsureStarted(output, Usage.Unavailable);
                WriteBlockDelta(output, delta);
                return;

            case IrStreamEvent.BlockStopped stop when _blocks.TryGetValue(stop.Index, out int index):
                if (_open.Remove(index))
                {
                    WriteBlockStop(output, index);
                }

                return;

            case IrStreamEvent.MessageEnded ended:
                _stopReason ??= ended.StopReason;
                return;

            default:
                return;
        }
    }

    /// <summary>关掉还开着的块，写出带最终用量的 <c>message_delta</c> 和 <c>message_stop</c>。</summary>
    public void Complete(Usage usage, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        EnsureStarted(output, Usage.Unavailable);
        foreach (int index in _open)
        {
            WriteBlockStop(output, index);
        }

        _open.Clear();

        IrStopReason stop = _stopReason ?? IrStopReason.EndTurn;
        if (_sawToolCall && stop == IrStopReason.EndTurn)
        {
            stop = IrStopReason.ToolUse;
        }

        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "message_delta");
        w.WriteStartObject("delta");
        w.WriteString("stop_reason", AnthropicWire.StopReason(stop));
        w.WriteNull("stop_sequence");
        w.WriteEndObject();
        AnthropicWire.WriteUsage(w, usage);
        w.WriteEndObject();
        Emit(output, "message_delta"u8);

        w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "message_stop");
        w.WriteEndObject();
        Emit(output, "message_stop"u8);

        Completed = true;
    }

    public void WriteError(IBufferWriter<byte> output, string message) => WriteError(output, "api_error", message);

    /// <summary>Anthropic 的流内错误事件。提交之后上游失败时只能这样告诉客户端。</summary>
    public static void WriteError(IBufferWriter<byte> output, string type, string message)
    {
        ArgumentNullException.ThrowIfNull(output);

        var json = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(json, AnthropicWire.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("type", "error");
            w.WriteStartObject("error");
            w.WriteString("type", type);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        output.Write("event: error\ndata: "u8);
        output.Write(json.WrittenSpan);
        output.Write("\n\n"u8);
    }

    public void Dispose() => _writer.Dispose();

    private void EnsureStarted(IBufferWriter<byte> output, Usage inputUsage)
    {
        if (Started)
        {
            return;
        }

        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "message_start");
        w.WriteStartObject("message");
        w.WriteString("id", _messageId);
        w.WriteString("type", "message");
        w.WriteString("role", "assistant");
        w.WriteString("model", _model ?? _fallbackModel);
        w.WriteStartArray("content");
        w.WriteEndArray();
        w.WriteNull("stop_reason");
        w.WriteNull("stop_sequence");

        // 上游的用量多半要到最后才有，开头给已知的那部分（Chat 上游是零），真实值在 message_delta 里。
        AnthropicWire.WriteUsage(w, inputUsage.HasValue ? inputUsage : default);
        w.WriteEndObject();
        w.WriteEndObject();
        Emit(output, "message_start"u8);
        Started = true;
    }

    private void WriteBlockStart(IBufferWriter<byte> output, IrStreamEvent.BlockStarted start)
    {
        if (_blocks.ContainsKey(start.Index))
        {
            return;
        }

        int index = _nextIndex++;
        _blocks[start.Index] = index;
        _open.Add(index);

        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "content_block_start");
        w.WriteNumber("index", index);
        w.WriteStartObject("content_block");
        switch (start.Kind)
        {
            case IrStreamBlockKind.Reasoning:
                // 签名留空，绝不伪造：只有真正的签发方能验证它。
                w.WriteString("type", "thinking");
                w.WriteString("thinking", string.Empty);
                w.WriteString("signature", string.Empty);
                break;

            case IrStreamBlockKind.RedactedReasoning:
                w.WriteString("type", "redacted_thinking");
                w.WriteString("data", start.Data ?? string.Empty);
                break;

            case IrStreamBlockKind.ToolCall:
                _sawToolCall = true;
                w.WriteString("type", "tool_use");
                w.WriteString("id", start.ToolCallId is { Length: > 0 } id ? id : _toolIdPrefix + index);
                w.WriteString("name", start.ToolName ?? string.Empty);
                w.WriteStartObject("input");
                w.WriteEndObject();
                break;

            default:
                // 拒答在 Anthropic 里没有单独的块，按普通文字写（Q193）。
                w.WriteString("type", "text");
                w.WriteString("text", string.Empty);
                break;
        }

        w.WriteEndObject();
        w.WriteEndObject();
        Emit(output, "content_block_start"u8);
    }

    private void WriteBlockDelta(IBufferWriter<byte> output, IrStreamEvent.BlockDelta delta)
    {
        // 遮蔽的推理没有增量形式，内容已经在 content_block_start 里。
        if (delta.Text.Length == 0 || delta.Kind == IrStreamBlockKind.RedactedReasoning)
        {
            return;
        }

        if (!_blocks.TryGetValue(delta.Index, out int index))
        {
            // 没见过开始事件的块：补一个，省得增量落在空处。
            WriteBlockStart(output, new IrStreamEvent.BlockStarted(delta.Index, delta.Kind));
            index = _blocks[delta.Index];
        }

        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "content_block_delta");
        w.WriteNumber("index", index);
        w.WriteStartObject("delta");
        switch (delta.Kind)
        {
            case IrStreamBlockKind.Reasoning:
                w.WriteString("type", "thinking_delta");
                w.WriteString("thinking", delta.Text);
                break;

            case IrStreamBlockKind.ToolCall:
                w.WriteString("type", "input_json_delta");
                w.WriteString("partial_json", delta.Text);
                break;

            default:
                w.WriteString("type", "text_delta");
                w.WriteString("text", delta.Text);
                break;
        }

        w.WriteEndObject();
        w.WriteEndObject();
        Emit(output, "content_block_delta"u8);
    }

    private void WriteBlockStop(IBufferWriter<byte> output, int index)
    {
        Utf8JsonWriter w = Begin();
        w.WriteStartObject();
        w.WriteString("type", "content_block_stop");
        w.WriteNumber("index", index);
        w.WriteEndObject();
        Emit(output, "content_block_stop"u8);
    }

    private Utf8JsonWriter Begin()
    {
        _json.ResetWrittenCount();
        _writer.Reset(_json);
        return _writer;
    }

    private void Emit(IBufferWriter<byte> output, ReadOnlySpan<byte> eventName)
    {
        _writer.Flush();
        output.Write("event: "u8);
        output.Write(eventName);
        output.Write("\ndata: "u8);
        output.Write(_json.WrittenSpan);
        output.Write("\n\n"u8);
    }
}
