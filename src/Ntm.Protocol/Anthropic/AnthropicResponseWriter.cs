using System.Buffers;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>写出结果：失败时给出原因，调用方按软错误处理（还没向客户端写任何字节，可以换候选）。</summary>
public readonly record struct AnthropicResponseWriting(byte[]? Body, string? Error)
{
    public bool Succeeded => Body is not null;
}

/// <summary>把 IR 写成 Anthropic Messages 的非流式响应。</summary>
public static class AnthropicResponseWriter
{
    /// <param name="messageId">完整的消息 id（含 <c>msg_</c> 前缀）。</param>
    /// <param name="fallbackModel">上游没报模型名时用它。</param>
    public static AnthropicResponseWriting Write(IrResponse response, string messageId, string fallbackModel)
    {
        ArgumentNullException.ThrowIfNull(response);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, AnthropicWire.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", messageId);
            writer.WriteString("type", "message");
            writer.WriteString("role", "assistant");
            writer.WriteString("model", response.Model ?? fallbackModel);

            writer.WriteStartArray("content");
            bool sawToolCall = false;
            foreach (IrBlock block in response.Content)
            {
                if (block is IrToolCall call)
                {
                    sawToolCall = true;
                    if (!TryWriteToolUse(writer, call))
                    {
                        return new AnthropicResponseWriting(null, $"工具 {call.Name} 的参数不是合法的 JSON 对象。");
                    }

                    continue;
                }

                WriteBlock(writer, block);
            }

            writer.WriteEndArray();

            IrStopReason stop = response.StopReason ?? IrStopReason.EndTurn;

            // 有些 OpenAI 兼容上游带了工具调用却报 stop；Anthropic 客户端靠 tool_use 决定去执行工具。
            if (sawToolCall && stop == IrStopReason.EndTurn)
            {
                stop = IrStopReason.ToolUse;
            }
            writer.WriteString("stop_reason", AnthropicWire.StopReason(stop));
            writer.WriteNull("stop_sequence");
            AnthropicWire.WriteUsage(writer, response.Usage);
            writer.WriteEndObject();
        }

        return new AnthropicResponseWriting(buffer.WrittenSpan.ToArray(), null);
    }

    private static void WriteBlock(Utf8JsonWriter writer, IrBlock block)
    {
        switch (block)
        {
            case IrText text:
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                writer.WriteEndObject();
                break;

            case IrReasoning reasoning:
                // 没有签名就写空串，绝不伪造：只有真正的签发方能验证它。
                writer.WriteStartObject();
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", reasoning.Text);
                writer.WriteString("signature", reasoning.SignedFor(ApiFormat.Anthropic) ? reasoning.Signature : string.Empty);
                writer.WriteEndObject();
                break;

            case IrRedactedReasoning redacted:
                writer.WriteStartObject();
                writer.WriteString("type", "redacted_thinking");
                writer.WriteString("data", redacted.Data);
                writer.WriteEndObject();
                break;

            default:
                // 响应里不会出现图片、文件和工具结果。
                break;
        }
    }

    /// <summary>Anthropic 的 input 是对象而不是字符串；上游给的参数解析不了就不能假装成功。</summary>
    private static bool TryWriteToolUse(Utf8JsonWriter writer, IrToolCall call)
    {
        string arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
        try
        {
            using var parsed = JsonDocument.Parse(arguments);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            writer.WriteStartObject();
            writer.WriteString("type", "tool_use");
            writer.WriteString("id", call.Id);
            writer.WriteString("name", call.Name);
            writer.WritePropertyName("input");
            parsed.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
