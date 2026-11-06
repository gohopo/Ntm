using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>写出结果：成功给字节，失败给原因。</summary>
public readonly record struct OpenAiChatResponseWriting(byte[]? Body, string? Error);

/// <summary>
/// 把 IR 写成 OpenAI Chat 的非流式响应（Q175）。
/// 文字块按顺序拼成 <c>content</c>，思考块进 <c>reasoning_content</c>，
/// 思考签名和 redacted_thinking 丢掉：OpenAI Chat 没有地方放它们，客户端也不会回传。
/// </summary>
public static class OpenAiChatResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>写出响应体。<paramref name="id"/> 用 <c>chatcmpl-</c> 前缀，<paramref name="created"/> 是 Unix 秒。</summary>
    public static OpenAiChatResponseWriting Write(IrResponse response, string id, string fallbackModel, long created)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        List<IrToolCall> toolCalls = [];

        foreach (IrBlock block in response.Content)
        {
            switch (block)
            {
                case IrText part:
                    text.Append(part.Text);
                    break;
                case IrReasoning part:
                    reasoning.Append(part.Text);
                    break;
                case IrToolCall call:
                    toolCalls.Add(call);
                    break;
            }
        }

        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("object", "chat.completion");
            writer.WriteNumber("created", created);
            writer.WriteString("model", response.Model ?? fallbackModel);

            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);

            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");

            // content 恒在：OpenAI 的客户端大多直接读它，没有文字时给 null 而不是省略。
            if (text.Length > 0)
            {
                writer.WriteString("content", text.ToString());
            }
            else
            {
                writer.WriteNull("content");
            }

            if (reasoning.Length > 0)
            {
                writer.WriteString("reasoning_content", reasoning.ToString());
            }

            if (toolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    IrToolCall call = toolCalls[i];
                    writer.WriteStartObject();
                    writer.WriteNumber("index", i);
                    writer.WriteString("id", call.Id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();

            writer.WriteString("finish_reason", FinishReason(response.StopReason, toolCalls.Count > 0));
            writer.WriteNull("logprobs");
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (response.Usage.HasValue)
            {
                WriteUsage(writer, response.Usage);
            }

            writer.WriteEndObject();
        }

        return new OpenAiChatResponseWriting(buffer.WrittenSpan.ToArray(), null);
    }

    /// <summary>
    /// OpenAI Chat 的 <c>prompt_tokens</c> 含缓存读与缓存写，和五项桶的拆法不同（ADR-0005），
    /// 所以这里加回去，缓存读另外放进 <c>prompt_tokens_details</c>。
    /// </summary>
    public static void WriteUsage(Utf8JsonWriter writer, Usage usage)
    {
        long prompt = usage.TotalInputTokens;
        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", prompt);
        writer.WriteNumber("completion_tokens", usage.OutputTokens);
        writer.WriteNumber("total_tokens", prompt + usage.OutputTokens);
        writer.WriteStartObject("prompt_tokens_details");
        writer.WriteNumber("cached_tokens", usage.CacheReadTokens);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>IR 的停止原因写回 OpenAI Chat 的 <c>finish_reason</c>。</summary>
    public static string FinishReason(IrStopReason? reason, bool hasToolCalls) => reason switch
    {
        IrStopReason.MaxTokens => "length",
        IrStopReason.ToolUse => "tool_calls",
        IrStopReason.Refusal => "content_filter",
        IrStopReason.EndTurn or IrStopReason.StopSequence => "stop",
        // 上游没给停止原因：有工具调用就按 tool_calls，否则按自然结束。
        _ => hasToolCalls ? "tool_calls" : "stop",
    };
}
