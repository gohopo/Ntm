using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 把 OpenAI Chat 的流式块读成 IR 事件（ADR-0015）。
/// <para>
/// Chat 的块没有显式的块边界：文字、推理、拒答靠字段区分，种类一变就要关掉上一个块。
/// 工具调用按上游的 <c>tool_calls[].index</c> 各自映射，交错到达的并行调用才不会落错块
/// （AxonHub 用单一递增序号，会落错块）。
/// </para>
/// <para>判定不在这里：帧是不是 ContentEvent、错误和用量都由 <see cref="OpenAiChatStreamInspector"/> 看。</para>
/// </summary>
public sealed class OpenAiChatStreamEventReader : IIrStreamEventReader
{
    private readonly Dictionary<int, int> _toolBlocks = [];
    private readonly Dictionary<string, int> _toolKeysById = new(StringComparer.Ordinal);
    private readonly List<int> _openToolBlocks = [];

    private string? _model;
    private string? _finishReason;
    private bool _started;
    private bool _ended;
    private bool _sawToolCall;
    private int _nextIndex;
    private IrStreamBlockKind? _open;
    private int _openIndex;
    private int _lastToolKey = -1;
    private int _syntheticToolKey = int.MaxValue / 2;

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
            if (root.ValueKind != JsonValueKind.Object || HasError(root))
            {
                return;
            }

            if (_model is null && String(root, "model") is { Length: > 0 } model)
            {
                _model = model;
            }

            // 只带用量的块（choices 为空）没有内容，用量由检视器记下，收尾时补。
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return;
            }

            if (!_started)
            {
                events.Add(new IrStreamEvent.MessageStarted(_model, Usage.Unavailable));
                _started = true;
            }

            JsonElement choice = choices[0];

            if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
            {
                string? reasoning = String(delta, "reasoning_content") ?? String(delta, "reasoning");
                if (reasoning is { Length: > 0 })
                {
                    Append(events, IrStreamBlockKind.Reasoning, reasoning, null);
                }

                if (String(delta, "content") is { Length: > 0 } text)
                {
                    Append(events, IrStreamBlockKind.Text, text, Logprobs(choice));
                }

                if (String(delta, "refusal") is { Length: > 0 } refusal)
                {
                    Append(events, IrStreamBlockKind.Refusal, refusal, null);
                }

                if (delta.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement call in toolCalls.EnumerateArray())
                    {
                        if (call.ValueKind == JsonValueKind.Object)
                        {
                            AppendToolCall(events, call);
                        }
                    }
                }
            }

            if (String(choice, "finish_reason") is { Length: > 0 } finish)
            {
                _finishReason = finish;
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
        CloseOpenBlock(events);
        CloseToolBlocks(events);

        IrStopReason stop = OpenAiChatFinishReason.ToIr(_finishReason) ?? IrStopReason.EndTurn;

        // 有些 OpenAI 兼容上游带了工具调用却报 stop；下游客户端靠工具调用决定去执行工具。
        if (_sawToolCall && stop == IrStopReason.EndTurn)
        {
            stop = IrStopReason.ToolUse;
        }

        events.Add(new IrStreamEvent.MessageEnded(stop));
    }

    private void Append(List<IrStreamEvent> events, IrStreamBlockKind kind, string text, string? logprobs)
    {
        if (_open != kind)
        {
            CloseOpenBlock(events);

            // 文字出现在工具调用之后，说明那几个调用已经写完。
            CloseToolBlocks(events);

            _openIndex = _nextIndex++;
            _open = kind;
            events.Add(new IrStreamEvent.BlockStarted(_openIndex, kind));
        }

        events.Add(new IrStreamEvent.BlockDelta(_openIndex, kind, text, logprobs));
    }

    private void AppendToolCall(List<IrStreamEvent> events, JsonElement call)
    {
        string? id = String(call, "id");
        string? name = null;
        string? arguments = null;
        if (call.TryGetProperty("function", out JsonElement function) && function.ValueKind == JsonValueKind.Object)
        {
            name = String(function, "name");
            arguments = String(function, "arguments");
        }

        int key = ToolKey(call, id);

        if (!_toolBlocks.TryGetValue(key, out int blockIndex))
        {
            CloseOpenBlock(events);
            blockIndex = _nextIndex++;
            _toolBlocks[key] = blockIndex;
            _openToolBlocks.Add(blockIndex);
            _sawToolCall = true;
            events.Add(new IrStreamEvent.BlockStarted(blockIndex, IrStreamBlockKind.ToolCall, id, name));
        }

        if (arguments is { Length: > 0 })
        {
            events.Add(new IrStreamEvent.BlockDelta(blockIndex, IrStreamBlockKind.ToolCall, arguments));
        }
    }

    /// <summary>
    /// 找出这个片段属于哪个工具调用。有 index 用 index；
    /// 有些上游（Gemini 的 OpenAI 兼容端点）每个并行调用整块发、不带 index，就按 id 区分；
    /// 两者都没有的片段接在上一个调用后面。
    /// </summary>
    private int ToolKey(JsonElement call, string? id)
    {
        int key;
        if (call.TryGetProperty("index", out JsonElement index) && index.ValueKind == JsonValueKind.Number
            && index.TryGetInt32(out int value))
        {
            key = value;
        }
        else if (id is { Length: > 0 })
        {
            if (!_toolKeysById.TryGetValue(id, out key))
            {
                key = _syntheticToolKey++;
                _toolKeysById[id] = key;
            }
        }
        else
        {
            key = _lastToolKey >= 0 ? _lastToolKey : _syntheticToolKey++;
        }

        _lastToolKey = key;
        return key;
    }

    private void CloseOpenBlock(List<IrStreamEvent> events)
    {
        if (_open is { } kind)
        {
            events.Add(new IrStreamEvent.BlockStopped(_openIndex, kind));
            _open = null;
        }
    }

    private void CloseToolBlocks(List<IrStreamEvent> events)
    {
        foreach (int index in _openToolBlocks)
        {
            events.Add(new IrStreamEvent.BlockStopped(index, IrStreamBlockKind.ToolCall));
        }

        _openToolBlocks.Clear();
    }

    private static string? Logprobs(JsonElement choice) =>
        choice.TryGetProperty("logprobs", out JsonElement logprobs)
        && logprobs.ValueKind == JsonValueKind.Object
        && logprobs.TryGetProperty("content", out JsonElement content)
        && content.ValueKind == JsonValueKind.Array
            ? content.GetRawText()
            : null;

    private static bool HasError(JsonElement root) =>
        root.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.String;

    private static string? String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
