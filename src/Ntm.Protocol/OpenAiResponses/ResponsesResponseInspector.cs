using System.Text;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>读一次 OpenAI Responses 非流式响应的结果。</summary>
public readonly record struct ResponsesResponseReading
{
    /// <summary>响应体是错误，或者 <c>status:"failed"</c>。HTTP 可能仍然是 200。</summary>
    public bool IsError { get; init; }

    public bool IsMalformed { get; init; }

    /// <summary>有没有真实输出（文字、拒绝、推理、函数调用）。</summary>
    public bool HasOutput { get; init; }

    public bool SawThinking { get; init; }

    public Usage Usage { get; init; }

    /// <summary>响应的 status，<c>incomplete</c> 时换成 <c>incomplete_details.reason</c>。</summary>
    public string? FinishReason { get; init; }

    public string? ErrorPayload { get; init; }

    public string? UpstreamModel { get; init; }

    /// <summary>上游给的响应 id（ADR-0016）。</summary>
    public string? ResponseId { get; init; }

    /// <summary>读成 IR 的响应；错误或解析不了时为空。内置工具等 IR 装不下的输出项被跳过。</summary>
    public IrResponse? Response { get; init; }
}

/// <summary>读 OpenAI Responses 的非流式响应：判定、用量，并读成 IR 供转成别的格式。</summary>
public static class ResponsesResponseInspector
{
    /// <summary>
    /// 只读一个响应对象的 <c>status</c> 和 <c>usage</c>：按 id 取回 background 响应时用来回填用量（Q277）。
    /// 不是 JSON 对象时 Status 为 null。
    /// </summary>
    public static (string? Status, Usage Usage) ReadSettlement(ReadOnlySpan<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray(), ResponsesWire.DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, default);
            }

            ResponsesWire.TryReadUsage(root, out Usage usage, out _);
            return (ResponsesWire.String(root, "status"), usage);
        }
        catch (JsonException)
        {
            return (null, default);
        }
    }

    public static ResponsesResponseReading Read(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), ResponsesWire.DocumentOptions);
        }
        catch (JsonException)
        {
            return new ResponsesResponseReading { IsMalformed = true };
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ResponsesResponseReading { IsMalformed = true };
            }

            if (ResponsesWire.TryReadError(root, out string? error))
            {
                return new ResponsesResponseReading { IsError = true, ErrorPayload = error };
            }

            bool sawThinking = false;
            if (ResponsesWire.TryReadUsage(root, out Usage usage, out long reasoningTokens) && reasoningTokens > 0)
            {
                sawThinking = true;
            }

            var content = new List<IrBlock>();
            bool hasToolCalls = false;
            if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in output.EnumerateArray())
                {
                    switch (ResponsesWire.String(item, "type"))
                    {
                        case "message":
                            ReadMessage(item, content);
                            break;

                        case "function_call":
                            hasToolCalls = true;
                            content.Add(new IrToolCall(
                                ResponsesWire.String(item, "call_id") ?? ResponsesWire.String(item, "id") ?? string.Empty,
                                ResponsesWire.String(item, "name") ?? string.Empty,
                                ResponsesWire.String(item, "arguments") is { Length: > 0 } arguments ? arguments : "{}"));
                            break;

                        case "reasoning":
                            if (ResponsesWire.HasReasoning(item))
                            {
                                sawThinking = true;
                            }

                            content.Add(ReadReasoning(item));
                            break;

                        default:
                            // 内置工具调用等，IR 装不下；透传时原样留着，转换时丢掉。
                            break;
                    }
                }
            }

            bool hasOutput = content.Exists(block => block switch
            {
                IrText text => text.Text.Length > 0,
                IrReasoning reasoning => reasoning.Text.Length > 0 || reasoning.Signature is { Length: > 0 },
                _ => true,
            });

            IrStopReason? stop = ResponsesWire.StopReason(root, hasToolCalls);
            if (content.Exists(block => block is IrText { IsRefusal: true }))
            {
                stop = IrStopReason.Refusal;
            }

            string? status = ResponsesWire.String(root, "status");
            string? finish = status == "incomplete"
                && root.TryGetProperty("incomplete_details", out JsonElement details)
                && ResponsesWire.String(details, "reason") is { } reason
                    ? reason
                    : status;

            string? model = ResponsesWire.String(root, "model");
            return new ResponsesResponseReading
            {
                HasOutput = hasOutput,
                SawThinking = sawThinking,
                Usage = usage,
                FinishReason = finish,
                UpstreamModel = model,
                ResponseId = ResponsesWire.String(root, "id"),
                Response = new IrResponse
                {
                    Model = model,
                    Content = content,
                    StopReason = stop,
                    Usage = usage,
                },
            };
        }
    }

    /// <summary>
    /// 推理项：summary 和 content 的文字连起来当推理文本；加密内容当签名，
    /// 记下是 Responses 产生的，只有回到 Responses 时才写回去（Q186）。
    /// </summary>
    internal static IrReasoning ReadReasoning(JsonElement item)
    {
        var text = new StringBuilder();
        foreach (string field in (ReadOnlySpan<string>)["summary", "content"])
        {
            if (item.TryGetProperty(field, out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (ResponsesWire.String(part, "text") is { Length: > 0 } piece)
                    {
                        if (text.Length > 0)
                        {
                            text.Append("\n\n");
                        }

                        text.Append(piece);
                    }
                }
            }

            // summary 有字就不再拼 content：两者通常是同一段推理的两种形态。
            if (text.Length > 0)
            {
                break;
            }
        }

        return new IrReasoning(
            text.ToString(),
            ResponsesWire.String(item, "encrypted_content"),
            ApiFormat.OpenAiResponses,
            ResponsesWire.String(item, "id"));
    }

    private static void ReadMessage(JsonElement item, List<IrBlock> content)
    {
        if (!item.TryGetProperty("content", out JsonElement parts))
        {
            return;
        }

        if (parts.ValueKind == JsonValueKind.String)
        {
            content.Add(new IrText(parts.GetString() ?? string.Empty));
            return;
        }

        if (parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in parts.EnumerateArray())
        {
            switch (ResponsesWire.String(part, "type"))
            {
                case "output_text":
                    content.Add(new IrText(
                        ResponsesWire.String(part, "text") ?? string.Empty,
                        LogprobsJson: part.TryGetProperty("logprobs", out JsonElement logprobs) && logprobs.ValueKind == JsonValueKind.Array && logprobs.GetArrayLength() > 0
                            ? logprobs.GetRawText()
                            : null));
                    break;

                case "refusal":
                    content.Add(new IrText(ResponsesWire.String(part, "refusal") ?? string.Empty, IsRefusal: true));
                    break;
            }
        }
    }
}
