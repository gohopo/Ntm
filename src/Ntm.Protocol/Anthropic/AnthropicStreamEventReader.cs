using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 把 Anthropic Messages 的 SSE 读成 IR 事件（ADR-0015）。Anthropic 的块边界本来就是显式的，几乎一一对应。
/// 服务端工具（web_search 等）的块 IR 装不下，整块跳过。签名和引用增量不外传。
/// </summary>
public sealed class AnthropicStreamEventReader : IIrStreamEventReader
{
    private readonly Dictionary<int, (int Index, IrStreamBlockKind Kind)> _blocks = [];
    private readonly List<int> _open = [];

    private bool _started;
    private bool _ended;
    private int _nextIndex;

    public void Read(ReadOnlySpan<byte> data, List<IrStreamEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        ReadOnlySpan<byte> trimmed = data.Trim(" \t\r\n"u8);
        if (_ended || trimmed.IsEmpty || trimmed.SequenceEqual("[DONE]"u8))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (String(root, "type"))
            {
                case "message_start":
                    ReadMessageStart(root, events);
                    return;
                case "content_block_start":
                    ReadBlockStart(root, events);
                    return;
                case "content_block_delta":
                    ReadBlockDelta(root, events);
                    return;
                case "content_block_stop":
                    ReadBlockStop(root, events);
                    return;
                case "message_delta"
                    when root.TryGetProperty("delta", out JsonElement delta)
                        && delta.ValueKind == JsonValueKind.Object
                        && String(delta, "stop_reason") is { Length: > 0 } stopReason:
                    CloseAll(events);
                    _ended = true;
                    events.Add(new IrStreamEvent.MessageEnded(AnthropicStopReason.ToIr(stopReason)));
                    return;
                default:
                    // ping、message_stop、error：前两个没有内容，错误由检视器判断。
                    return;
            }
        }
    }

    public void Finish(List<IrStreamEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_ended)
        {
            return;
        }

        _ended = true;
        CloseAll(events);
        events.Add(new IrStreamEvent.MessageEnded(null));
    }

    private void ReadMessageStart(JsonElement root, List<IrStreamEvent> events)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        string? model = null;
        Usage usage = Usage.Unavailable;
        if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
        {
            model = String(message, "model");
            if (AnthropicUsageReader.TryRead(message, out Usage read))
            {
                usage = read;
            }
        }

        events.Add(new IrStreamEvent.MessageStarted(model, usage));
    }

    private void ReadBlockStart(JsonElement root, List<IrStreamEvent> events)
    {
        if (!TryIndex(root, out int upstreamIndex)
            || !root.TryGetProperty("content_block", out JsonElement block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        IrStreamEvent.BlockStarted? start = String(block, "type") switch
        {
            "text" => new IrStreamEvent.BlockStarted(_nextIndex, IrStreamBlockKind.Text),
            "thinking" => new IrStreamEvent.BlockStarted(_nextIndex, IrStreamBlockKind.Reasoning),
            "redacted_thinking" => new IrStreamEvent.BlockStarted(
                _nextIndex, IrStreamBlockKind.RedactedReasoning, Data: String(block, "data")),
            "tool_use" => new IrStreamEvent.BlockStarted(
                _nextIndex, IrStreamBlockKind.ToolCall, String(block, "id"), String(block, "name")),
            _ => null,
        };

        if (start is null)
        {
            return;
        }

        _nextIndex++;
        _blocks[upstreamIndex] = (start.Index, start.Kind);
        _open.Add(upstreamIndex);
        events.Add(start);

        // 有的上游把开头的文字直接放在 content_block_start 里。
        if (start.Kind == IrStreamBlockKind.Text && String(block, "text") is { Length: > 0 } text)
        {
            events.Add(new IrStreamEvent.BlockDelta(start.Index, start.Kind, text));
        }
    }

    private void ReadBlockDelta(JsonElement root, List<IrStreamEvent> events)
    {
        if (!TryIndex(root, out int upstreamIndex)
            || !_blocks.TryGetValue(upstreamIndex, out (int Index, IrStreamBlockKind Kind) block)
            || !root.TryGetProperty("delta", out JsonElement delta)
            || delta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? text = String(delta, "type") switch
        {
            "text_delta" => String(delta, "text"),
            "thinking_delta" => String(delta, "thinking"),
            "input_json_delta" => String(delta, "partial_json"),
            _ => null,
        };

        if (text is { Length: > 0 })
        {
            events.Add(new IrStreamEvent.BlockDelta(block.Index, block.Kind, text));
        }
    }

    private void ReadBlockStop(JsonElement root, List<IrStreamEvent> events)
    {
        if (TryIndex(root, out int upstreamIndex)
            && _open.Remove(upstreamIndex)
            && _blocks.TryGetValue(upstreamIndex, out (int Index, IrStreamBlockKind Kind) block))
        {
            events.Add(new IrStreamEvent.BlockStopped(block.Index, block.Kind));
        }
    }

    private void CloseAll(List<IrStreamEvent> events)
    {
        foreach (int upstreamIndex in _open)
        {
            (int index, IrStreamBlockKind kind) = _blocks[upstreamIndex];
            events.Add(new IrStreamEvent.BlockStopped(index, kind));
        }

        _open.Clear();
    }

    private static bool TryIndex(JsonElement root, out int index)
    {
        index = 0;
        return root.TryGetProperty("index", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out index);
    }

    private static string? String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
