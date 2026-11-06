using System.Buffers;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>写一个 Responses 响应对象的结果。</summary>
public readonly record struct ResponsesResponseWriting(byte[]? Body, string? Error);

/// <summary>
/// IR 响应写成 OpenAI Responses 的 <c>response</c> 对象（非流式，也给流式的终止事件用）。
/// 连续的文字 / 拒绝合成一个 message 项；推理写成 reasoning 项，只有 Responses 自己产生的才带加密内容（Q186）。
/// </summary>
public static class ResponsesResponseWriter
{
    public static ResponsesResponseWriting Write(IrResponse response, string responseId, string fallbackModel, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(responseId);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, ResponsesWire.WriterOptions))
        {
            WriteObject(writer, responseId, response.Model ?? fallbackModel, createdAt, response.Content, response.StopReason, response.Usage);
        }

        return new ResponsesResponseWriting(buffer.WrittenSpan.ToArray(), null);
    }

    /// <summary>写整个响应对象。流式写出器在 <c>response.completed</c> 里也用这个。</summary>
    internal static void WriteObject(
        Utf8JsonWriter writer,
        string responseId,
        string model,
        long createdAt,
        IReadOnlyList<IrBlock> content,
        IrStopReason? stopReason,
        Usage usage)
    {
        (string status, string? incomplete) = ResponsesWire.Status(stopReason);

        writer.WriteStartObject();
        writer.WriteString("id", responseId);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", createdAt);
        writer.WriteString("status", status);
        writer.WriteNull("error");
        if (incomplete is null)
        {
            writer.WriteNull("incomplete_details");
        }
        else
        {
            writer.WriteStartObject("incomplete_details");
            writer.WriteString("reason", incomplete);
            writer.WriteEndObject();
        }

        writer.WriteString("model", model);
        writer.WriteStartArray("output");
        WriteOutput(writer, responseId, content);
        writer.WriteEndArray();
        writer.WriteBoolean("parallel_tool_calls", true);
        writer.WriteNull("previous_response_id");
        writer.WriteBoolean("store", false);
        if (usage.HasValue)
        {
            ResponsesWire.WriteUsage(writer, usage);
        }
        else
        {
            writer.WriteNull("usage");
        }

        writer.WriteEndObject();
    }

    private static void WriteOutput(Utf8JsonWriter writer, string responseId, IReadOnlyList<IrBlock> content)
    {
        int sequence = 0;
        string suffix = responseId.StartsWith("resp_", StringComparison.Ordinal) ? responseId[5..] : responseId;
        var parts = new List<IrText>();

        void FlushMessage()
        {
            if (parts.Count == 0)
            {
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("id", $"msg_{suffix}_{sequence++}");
            writer.WriteString("type", "message");
            writer.WriteString("role", "assistant");
            writer.WriteString("status", "completed");
            writer.WriteStartArray("content");
            foreach (IrText part in parts)
            {
                WritePart(writer, part);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            parts.Clear();
        }

        foreach (IrBlock block in content)
        {
            switch (block)
            {
                case IrText text:
                    parts.Add(text);
                    break;

                case IrReasoning reasoning:
                    FlushMessage();
                    writer.WriteStartObject();
                    writer.WriteString("id", reasoning.SignedFor(ApiFormat.OpenAiResponses) && reasoning.ItemId is { Length: > 0 } id
                        ? id
                        : $"rs_{suffix}_{sequence}");
                    sequence++;
                    writer.WriteString("type", "reasoning");
                    writer.WriteStartArray("summary");
                    if (reasoning.Text.Length > 0)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "summary_text");
                        writer.WriteString("text", reasoning.Text);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    if (reasoning.SignedFor(ApiFormat.OpenAiResponses))
                    {
                        writer.WriteString("encrypted_content", reasoning.Signature);
                    }

                    writer.WriteEndObject();
                    break;

                case IrToolCall call:
                    FlushMessage();
                    writer.WriteStartObject();
                    writer.WriteString("id", $"fc_{suffix}_{sequence++}");
                    writer.WriteString("type", "function_call");
                    writer.WriteString("status", "completed");
                    writer.WriteString("call_id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    break;

                default:
                    // 遮蔽的推理等 Responses 装不下的块，丢掉。
                    break;
            }
        }

        FlushMessage();
    }

    internal static void WritePart(Utf8JsonWriter writer, IrText part)
    {
        writer.WriteStartObject();
        if (part.IsRefusal)
        {
            writer.WriteString("type", "refusal");
            writer.WriteString("refusal", part.Text);
        }
        else
        {
            writer.WriteString("type", "output_text");
            writer.WriteString("text", part.Text);
            writer.WriteStartArray("annotations");
            writer.WriteEndArray();
            if (part.LogprobsJson is not null)
            {
                writer.WritePropertyName("logprobs");
                writer.WriteRawValue(part.LogprobsJson, skipInputValidation: true);
            }
        }

        writer.WriteEndObject();
    }
}
