using System.Text.Json;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>
/// 逐帧读 OpenAI Responses 的流式响应，判定 ContentEvent、提取 Usage、认出终止与错误。
/// 每个数据帧自带 <c>type</c>，不看 <c>event:</c> 行（规范没写清有没有，Q197）。
/// 只读：字节已经在去客户端的路上（ADR-0002）。
/// </summary>
public sealed class ResponsesStreamInspector
{
    /// <summary>见过第一个 ContentEvent 没有。</summary>
    public bool HasContentEvent { get; private set; }

    /// <summary>见过 <c>response.completed</c> / <c>response.incomplete</c> 没有。Responses 没有 <c>[DONE]</c>。</summary>
    public bool SawTerminal { get; private set; }

    /// <summary>用量只在终止事件的 <c>response.usage</c> 里。</summary>
    public Usage Usage { get; private set; }

    /// <summary>终止时响应的状态（completed / incomplete），用来当结束原因记下来。</summary>
    public string? FinishReason { get; private set; }

    public string? ErrorPayload { get; private set; }

    public string? UpstreamModel { get; private set; }

    /// <summary>上游给这次响应起的 id（<c>resp_…</c>）。previous_response_id 要靠它找回落点（ADR-0016）。</summary>
    public string? ResponseId { get; private set; }

    public long OutputAsciiChars { get; private set; }

    public long OutputWideChars { get; private set; }

    /// <summary>见过有内容的推理项，或者用量里推理 Token 大于 0（Q191）。</summary>
    public bool SawThinking { get; private set; }

    public bool EndedEmpty => SawTerminal && !HasContentEvent && Usage.OutputTokens == 0;

    public StreamFrameVerdict Inspect(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> trimmed = data.Trim(" \t\r\n"u8);
        if (trimmed.IsEmpty || trimmed.SequenceEqual("[DONE]"u8))
        {
            return StreamFrameVerdict.Preamble;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed.ToArray(), ResponsesWire.DocumentOptions);
        }
        catch (JsonException)
        {
            return StreamFrameVerdict.Malformed;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return StreamFrameVerdict.Malformed;
            }

            string? type = ResponsesWire.String(root, "type");
            switch (type)
            {
                case "error":
                    ErrorPayload = root.GetRawText();
                    return StreamFrameVerdict.Error;

                case "response.failed":
                    ErrorPayload = root.TryGetProperty("response", out JsonElement failed)
                        && failed.TryGetProperty("error", out JsonElement failure)
                        && failure.ValueKind == JsonValueKind.Object
                            ? failure.GetRawText()
                            : root.GetRawText();
                    ReadResponse(root);
                    return StreamFrameVerdict.Error;

                case "response.created" or "response.in_progress" or "response.queued":
                    ReadResponse(root);
                    return StreamFrameVerdict.Preamble;

                case "response.completed" or "response.incomplete":
                    ReadResponse(root);
                    SawTerminal = true;
                    return StreamFrameVerdict.Terminal;

                case "response.output_item.added":
                    return InspectItemAdded(root);

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out JsonElement done)
                        && ResponsesWire.String(done, "type") == "reasoning"
                        && ResponsesWire.HasReasoning(done))
                    {
                        SawThinking = true;
                    }

                    return StreamFrameVerdict.Preamble;

                case "response.output_text.delta"
                    or "response.refusal.delta"
                    or "response.function_call_arguments.delta":
                    return CountDelta(root, thinking: false);

                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta":
                    return CountDelta(root, thinking: true);

                default:
                    // content_part.*、*.done、内置工具事件、注释等：发出去无妨，不据此判定。
                    return StreamFrameVerdict.Preamble;
            }
        }
    }

    private void ReadResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out JsonElement response) || response.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        ResponseId ??= ResponsesWire.String(response, "id");
        if (ResponsesWire.String(response, "model") is { Length: > 0 } model)
        {
            UpstreamModel = model;
        }

        if (ResponsesWire.String(response, "status") is { } status and not ("in_progress" or "queued"))
        {
            FinishReason = status == "incomplete"
                && response.TryGetProperty("incomplete_details", out JsonElement details)
                && ResponsesWire.String(details, "reason") is { } reason
                    ? reason
                    : status;
        }

        if (ResponsesWire.TryReadUsage(response, out Usage usage, out long reasoningTokens))
        {
            Usage = usage;
            if (reasoningTokens > 0)
            {
                SawThinking = true;
            }
        }
    }

    private StreamFrameVerdict InspectItemAdded(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return StreamFrameVerdict.Preamble;
        }

        // 函数调用的名字和 call_id 只在 added 里，按 Anthropic 的 tool_use 开块一样算第一个内容（Q167 的同类规则）。
        if (ResponsesWire.String(item, "type") == "function_call")
        {
            HasContentEvent = true;
            return StreamFrameVerdict.Content;
        }

        return StreamFrameVerdict.Preamble;
    }

    private StreamFrameVerdict CountDelta(JsonElement root, bool thinking)
    {
        string? delta = ResponsesWire.String(root, "delta");
        if (string.IsNullOrEmpty(delta))
        {
            return StreamFrameVerdict.Preamble;
        }

        foreach (char c in delta)
        {
            if (char.IsAscii(c))
            {
                OutputAsciiChars++;
            }
            else if (!char.IsLowSurrogate(c))
            {
                OutputWideChars++;
            }
        }

        if (thinking)
        {
            SawThinking = true;
        }

        HasContentEvent = true;
        return StreamFrameVerdict.Content;
    }
}
