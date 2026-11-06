using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>OpenAI Chat 的 finish_reason 与 IR 停止原因之间的换算。</summary>
public static class OpenAiChatFinishReason
{
    /// <summary>
    /// OpenAI 对自然结束和命中 stop 序列都给 <c>stop</c>，分不出来，统一算自然结束。
    /// 认不出的值也算自然结束：总要给客户端一个停止原因。
    /// </summary>
    public static IrStopReason? ToIr(string? finishReason) => finishReason switch
    {
        null => null,
        "length" => IrStopReason.MaxTokens,
        "tool_calls" or "function_call" => IrStopReason.ToolUse,
        "content_filter" => IrStopReason.Refusal,
        _ => IrStopReason.EndTurn,
    };
}

/// <summary>把 OpenAI Chat 的非流式响应读成 IR。只看第一个 choice：网关不发 n &gt; 1 的请求。</summary>
public static class OpenAiChatResponseConverter
{
    /// <summary>解析不了或没有 choice 时返回 null。</summary>
    public static IrResponse? ToIr(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement choice = choices[0];
            var content = new List<IrBlock>();
            bool refused = false;

            if (choice.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
            {
                // 推理在前：上游先想后说，Anthropic 的块顺序也是如此。
                string? reasoning = String(message, "reasoning_content") ?? String(message, "reasoning");
                if (reasoning is { Length: > 0 })
                {
                    content.Add(new IrReasoning(reasoning, null, ApiFormat.OpenAiChat));
                }

                if (String(message, "content") is { Length: > 0 } text)
                {
                    content.Add(new IrText(text));
                }

                if (String(message, "refusal") is { Length: > 0 } refusal)
                {
                    content.Add(new IrText(refusal, IsRefusal: true));
                    refused = true;
                }

                if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement call in toolCalls.EnumerateArray())
                    {
                        if (call.ValueKind != JsonValueKind.Object
                            || !call.TryGetProperty("function", out JsonElement function)
                            || function.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        content.Add(new IrToolCall(
                            String(call, "id") ?? string.Empty,
                            String(function, "name") ?? string.Empty,
                            String(function, "arguments") ?? string.Empty));
                    }
                }
            }

            IrStopReason? stop = OpenAiChatFinishReason.ToIr(String(choice, "finish_reason"));
            return new IrResponse
            {
                Model = String(root, "model"),
                Content = content,
                StopReason = refused ? IrStopReason.Refusal : stop,
                Usage = OpenAiChatResponseInspector.TryReadUsage(root, out Usage usage) ? usage : Usage.Unavailable,
            };
        }
    }

    private static string? String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
