using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>
/// 把 OpenAI Responses 的 SSE 读成 IR 流事件（ADR-0015）。
/// 块按 (item_id, content_index) 归并：文字、拒绝、推理各占一个块，函数调用按 item 占一个块。
/// 内置工具事件、注释、obfuscation 字段都跳过。
/// </summary>
public sealed class ResponsesStreamEventReader : IIrStreamEventReader
{
    private readonly Dictionary<string, (int Index, IrStreamBlockKind Kind)> _blocks = [];
    private readonly List<string> _open = [];

    private bool _started;
    private bool _ended;
    private int _nextIndex;
    private bool _sawToolCall;

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
            document = JsonDocument.Parse(trimmed.ToArray(), ResponsesWire.DocumentOptions);
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

            switch (ResponsesWire.String(root, "type"))
            {
                case "response.created":
                    Start(root, events);
                    return;

                case "response.output_item.added":
                    ReadItemAdded(root, events);
                    return;

                case "response.output_item.done":
                    CloseItem(Key(root, ItemId(root)), events);
                    return;

                case "response.output_text.delta":
                    Delta(root, events, IrStreamBlockKind.Text, ContentKey(root), logprobs: true);
                    return;

                case "response.refusal.delta":
                    Delta(root, events, IrStreamBlockKind.Refusal, ContentKey(root), logprobs: false);
                    return;

                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta":
                    Delta(root, events, IrStreamBlockKind.Reasoning, ReasoningKey(root), logprobs: false);
                    return;

                case "response.content_part.done":
                    Close(ContentKey(root), events);
                    return;

                case "response.reasoning_summary_part.done":
                    Close(ReasoningKey(root), events);
                    return;

                case "response.function_call_arguments.delta":
                    Delta(root, events, IrStreamBlockKind.ToolCall, Key(root, ResponsesWire.String(root, "item_id")), logprobs: false);
                    return;

                case "response.completed" or "response.incomplete":
                    End(root, events);
                    return;

                default:
                    // in_progress、queued、content_part.added、*.done 的文本回显、内置工具事件：没有新内容。
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

    private void Start(JsonElement root, List<IrStreamEvent> events)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        string? model = root.TryGetProperty("response", out JsonElement response)
            ? ResponsesWire.String(response, "model")
            : null;

        // Responses 的用量只在终止事件里，开头拿不到输入侧的真数。
        events.Add(new IrStreamEvent.MessageStarted(model, Usage.Unavailable));
    }

    private void ReadItemAdded(JsonElement root, List<IrStreamEvent> events)
    {
        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (ResponsesWire.String(item, "type"))
        {
            case "function_call":
                {
                    _sawToolCall = true;
                    string key = Key(root, ResponsesWire.String(item, "id"));
                    Open(
                        key,
                        IrStreamBlockKind.ToolCall,
                        events,
                        ResponsesWire.String(item, "call_id") ?? ResponsesWire.String(item, "id"),
                        ResponsesWire.String(item, "name"));

                    if (ResponsesWire.String(item, "arguments") is { Length: > 0 } arguments
                        && _blocks.TryGetValue(key, out (int Index, IrStreamBlockKind Kind) block))
                    {
                        events.Add(new IrStreamEvent.BlockDelta(block.Index, block.Kind, arguments));
                    }

                    return;
                }

            default:
                // message 和 reasoning 项等第一个增量再开块：那时才知道是文字、拒绝还是推理。
                return;
        }
    }

    private void Delta(JsonElement root, List<IrStreamEvent> events, IrStreamBlockKind kind, string key, bool logprobs)
    {
        string? text = ResponsesWire.String(root, "delta");
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!_blocks.TryGetValue(key, out (int Index, IrStreamBlockKind Kind) block))
        {
            Open(key, kind, events, null, null);
            block = _blocks[key];
        }

        events.Add(new IrStreamEvent.BlockDelta(
            block.Index,
            block.Kind,
            text,
            logprobs && root.TryGetProperty("logprobs", out JsonElement node) && node.ValueKind == JsonValueKind.Array && node.GetArrayLength() > 0
                ? node.GetRawText()
                : null));
    }

    private void Open(string key, IrStreamBlockKind kind, List<IrStreamEvent> events, string? toolCallId, string? toolName)
    {
        if (_blocks.ContainsKey(key))
        {
            return;
        }

        int index = _nextIndex++;
        _blocks[key] = (index, kind);
        _open.Add(key);
        events.Add(new IrStreamEvent.BlockStarted(index, kind, toolCallId, toolName));
    }

    private void Close(string key, List<IrStreamEvent> events)
    {
        if (_open.Remove(key) && _blocks.TryGetValue(key, out (int Index, IrStreamBlockKind Kind) block))
        {
            events.Add(new IrStreamEvent.BlockStopped(block.Index, block.Kind));
        }
    }

    /// <summary>一个输出项结束：它名下的块（文字的各个 content_index、推理）一起关掉。</summary>
    private void CloseItem(string itemKey, List<IrStreamEvent> events)
    {
        foreach (string key in _open.ToArray())
        {
            if (key == itemKey
                || key.StartsWith(itemKey + "#", StringComparison.Ordinal)
                || key.StartsWith(itemKey + "@", StringComparison.Ordinal))
            {
                Close(key, events);
            }
        }
    }

    private void End(JsonElement root, List<IrStreamEvent> events)
    {
        _ended = true;
        CloseAll(events);
        IrStopReason? stop = root.TryGetProperty("response", out JsonElement response)
            ? ResponsesWire.StopReason(response, _sawToolCall)
            : null;
        events.Add(new IrStreamEvent.MessageEnded(stop));
    }

    private void CloseAll(List<IrStreamEvent> events)
    {
        foreach (string key in _open)
        {
            (int index, IrStreamBlockKind kind) = _blocks[key];
            events.Add(new IrStreamEvent.BlockStopped(index, kind));
        }

        _open.Clear();
    }

    private static string ContentKey(JsonElement root) =>
        Key(root, ResponsesWire.String(root, "item_id")) + "#" + (ResponsesWire.Int(root, "content_index") ?? 0);

    private static string ReasoningKey(JsonElement root) =>
        Key(root, ResponsesWire.String(root, "item_id")) + "@reasoning";

    /// <summary>item_id 不在时退回 output_index：同一个项的事件至少还能归到一起。</summary>
    private static string Key(JsonElement root, string? itemId) =>
        itemId is { Length: > 0 } ? itemId : "#" + (ResponsesWire.Int(root, "output_index") ?? 0);

    private static string? ItemId(JsonElement root) =>
        root.TryGetProperty("item", out JsonElement item) ? ResponsesWire.String(item, "id") : null;
}
