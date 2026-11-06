using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 把 IR 写成 OpenAI Chat Completions 请求体。
/// OpenAI Chat 的消息是扁平的，这里会丢掉两样东西，都是这个格式本身表达不了的（ADR-0005）：
/// 推理块（Chat 没有回传推理的位置，DeepSeek 甚至要求历史里不带它），
/// 以及同一条助手消息里文字与工具调用的先后顺序。
/// </summary>
public static class OpenAiChatRequestWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 中文按原样写出，不转成 \uXXXX：这是发给上游的 JSON，不会嵌进 HTML。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 写出请求体。流式时总是带上 <c>stream_options.include_usage</c>：
    /// 不然转换路径拿不到用量，计费会落空。
    /// </summary>
    public static byte[] Write(IrRequest request, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", upstreamModel);
            WriteMessages(writer, request);

            if (request.MaxOutputTokens is { } maxTokens)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            if (request.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP)
            {
                writer.WriteNumber("top_p", topP);
            }

            if (request.StopSequences.Count > 0)
            {
                writer.WriteStartArray("stop");
                foreach (string stop in request.StopSequences)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            WriteTools(writer, request);

            if (request.EndUserId is { } user)
            {
                writer.WriteString("user", user);
            }

            WriteResponseFormat(writer, request.ResponseFormat);

            if (request.Logprobs is { } top)
            {
                writer.WriteBoolean("logprobs", true);
                if (top > 0)
                {
                    writer.WriteNumber("top_logprobs", top);
                }
            }

            if (Anthropic.AnthropicThinking.OpenAiEffort(request) is { } effort)
            {
                writer.WriteString("reasoning_effort", effort);
            }

            if (request.Stream)
            {
                writer.WriteBoolean("stream", true);
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessages(Utf8JsonWriter writer, IrRequest request)
    {
        writer.WriteStartArray("messages");

        if (request.System.Count > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", string.Join("\n\n", request.System));
            writer.WriteEndObject();
        }

        foreach (IrMessage message in request.Messages)
        {
            if (message.Role == IrRole.Assistant)
            {
                WriteAssistant(writer, message.Content);
            }
            else
            {
                WriteUser(writer, message.Content);
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 用户消息里的 tool_result 拆成独立的 tool 消息，并排在前面：
    /// OpenAI 要求 tool 消息紧跟在带 tool_calls 的助手消息后面。
    /// </summary>
    private static void WriteUser(Utf8JsonWriter writer, IReadOnlyList<IrBlock> content)
    {
        var rest = new List<IrBlock>(content.Count);
        foreach (IrBlock block in content)
        {
            if (block is IrToolResult result)
            {
                WriteToolMessage(writer, result, rest);
            }
            else
            {
                rest.Add(block);
            }
        }

        if (rest.Count == 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "user");
        WriteUserContent(writer, rest);
        writer.WriteEndObject();
    }

    /// <summary>tool 消息只能放文字；结果里的图片和文件挪到随后的 user 消息里。</summary>
    private static void WriteToolMessage(Utf8JsonWriter writer, IrToolResult result, List<IrBlock> followUp)
    {
        var text = new List<string>();
        foreach (IrBlock block in result.Content)
        {
            if (block is IrText t)
            {
                text.Add(t.Text);
            }
            else
            {
                followUp.Add(block);
            }
        }

        writer.WriteStartObject();
        writer.WriteString("role", "tool");
        writer.WriteString("tool_call_id", result.ToolCallId);
        writer.WriteString("content", string.Join("\n", text));
        writer.WriteEndObject();
    }

    /// <summary>纯文字时写成字符串：有些 OpenAI 兼容上游不接受数组形式的 content。</summary>
    private static void WriteUserContent(Utf8JsonWriter writer, List<IrBlock> blocks)
    {
        if (blocks.TrueForAll(b => b is IrText))
        {
            writer.WriteString("content", string.Concat(blocks.Select(b => ((IrText)b).Text)));
            return;
        }

        writer.WriteStartArray("content");
        foreach (IrBlock block in blocks)
        {
            switch (block)
            {
                case IrText text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;

                case IrImage image:
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString(
                        "url",
                        image.Url?.AbsoluteUri ?? $"data:{image.MediaType};base64,{image.Base64Data}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                case IrFile file:
                    writer.WriteStartObject();
                    writer.WriteString("type", "file");
                    writer.WriteStartObject("file");
                    writer.WriteString("filename", file.FileName ?? "document");
                    writer.WriteString("file_data", $"data:{file.MediaType};base64,{file.Base64Data}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                default:
                    // 推理块不属于用户消息，读取方已经保证不会出现别的类型。
                    break;
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteAssistant(Utf8JsonWriter writer, IReadOnlyList<IrBlock> content)
    {
        string text = string.Concat(content.OfType<IrText>().Where(t => !t.IsRefusal).Select(t => t.Text));
        string refusal = string.Concat(content.OfType<IrText>().Where(t => t.IsRefusal).Select(t => t.Text));
        List<IrToolCall> toolCalls = [.. content.OfType<IrToolCall>()];

        // 只有推理的助手消息在 Chat 里没有内容可写。
        if (text.Length == 0 && refusal.Length == 0 && toolCalls.Count == 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        if (text.Length > 0)
        {
            writer.WriteString("content", text);
        }
        else
        {
            writer.WriteNull("content");
        }

        if (refusal.Length > 0)
        {
            writer.WriteString("refusal", refusal);
        }

        if (toolCalls.Count > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (IrToolCall call in toolCalls)
            {
                writer.WriteStartObject();
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
    }

    private static void WriteResponseFormat(Utf8JsonWriter writer, IrResponseFormat? format)
    {
        if (format is null)
        {
            return;
        }

        writer.WriteStartObject("response_format");
        if (format.Kind == IrResponseFormatKind.JsonObject)
        {
            writer.WriteString("type", "json_object");
        }
        else
        {
            writer.WriteString("type", "json_schema");
            writer.WriteStartObject("json_schema");
            writer.WriteString("name", format.Name ?? "response");
            if (format.Description is { } description)
            {
                writer.WriteString("description", description);
            }

            if (format.SchemaJson is { } schema)
            {
                writer.WritePropertyName("schema");
                writer.WriteRawValue(schema, skipInputValidation: false);
            }

            if (format.Strict is { } strict)
            {
                writer.WriteBoolean("strict", strict);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, IrRequest request)
    {
        if (request.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            foreach (IrTool tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", tool.Name);
                if (tool.Description is { } description)
                {
                    writer.WriteString("description", description);
                }

                writer.WritePropertyName("parameters");
                writer.WriteRawValue(tool.ParametersSchemaJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (request.ToolChoice is not { } choice)
        {
            return;
        }

        switch (choice.Mode)
        {
            case IrToolChoiceMode.Auto:
                writer.WriteString("tool_choice", "auto");
                break;
            case IrToolChoiceMode.Any:
                writer.WriteString("tool_choice", "required");
                break;
            case IrToolChoiceMode.None:
                writer.WriteString("tool_choice", "none");
                break;
            case IrToolChoiceMode.Specific:
                writer.WriteStartObject("tool_choice");
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", choice.Name);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
        }

        if (choice.DisableParallelToolUse && request.Tools.Count > 0)
        {
            writer.WriteBoolean("parallel_tool_calls", false);
        }
    }
}
