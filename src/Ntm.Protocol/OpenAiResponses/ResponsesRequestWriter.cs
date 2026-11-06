using System.Buffers;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>
/// IR 写成 OpenAI Responses 的请求体，用于 Chat / Anthropic 入站转给 Responses 上游。
/// system 合进 <c>instructions</c>（Q187）；一律 <c>store:false</c> 并要回加密推理，
/// 因为这条链路的客户端不会用 previous_response_id（Q184）。
/// </summary>
public static class ResponsesRequestWriter
{
    /// <param name="request">IR 请求。</param>
    /// <param name="upstreamModel">替换后的上游模型名。</param>
    /// <param name="promptCacheKey">提示缓存的分组键，用 Conversation 的身份（Q192）；为空就不写。</param>
    public static byte[] Write(IrRequest request, string upstreamModel, string? promptCacheKey)
    {
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, ResponsesWire.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("model", upstreamModel);

            List<string> system = request.System.Where(part => part.Length > 0).ToList();
            if (system.Count > 0)
            {
                w.WriteString("instructions", string.Join("\n\n", system));
            }

            w.WriteStartArray("input");
            foreach (IrMessage message in request.Messages)
            {
                WriteMessage(w, message);
            }

            w.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (IrTool tool in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteString("name", tool.Name);
                    if (tool.Description is { Length: > 0 } description)
                    {
                        w.WriteString("description", description);
                    }

                    w.WritePropertyName("parameters");
                    WriteRawOrEmptyObject(w, tool.ParametersSchemaJson);
                    w.WriteBoolean("strict", false);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (request.ToolChoice is { } choice)
            {
                switch (choice.Mode)
                {
                    case IrToolChoiceMode.None:
                        w.WriteString("tool_choice", "none");
                        break;
                    case IrToolChoiceMode.Any:
                        w.WriteString("tool_choice", "required");
                        break;
                    case IrToolChoiceMode.Specific when choice.Name is { Length: > 0 } name:
                        w.WriteStartObject("tool_choice");
                        w.WriteString("type", "function");
                        w.WriteString("name", name);
                        w.WriteEndObject();
                        break;
                    default:
                        if (request.Tools.Count > 0)
                        {
                            w.WriteString("tool_choice", "auto");
                        }

                        break;
                }

                if (choice.DisableParallelToolUse)
                {
                    w.WriteBoolean("parallel_tool_calls", false);
                }
            }

            if (request.MaxOutputTokens is { } max)
            {
                w.WriteNumber("max_output_tokens", max);
            }

            if (request.Temperature is { } temperature)
            {
                w.WriteNumber("temperature", temperature);
            }

            if (request.TopP is { } topP)
            {
                w.WriteNumber("top_p", topP);
            }

            if (request.ResponseFormat is { } format)
            {
                w.WriteStartObject("text");
                w.WriteStartObject("format");
                if (format.Kind == IrResponseFormatKind.JsonObject)
                {
                    w.WriteString("type", "json_object");
                }
                else
                {
                    w.WriteString("type", "json_schema");
                    w.WriteString("name", format.Name is { Length: > 0 } name ? name : "response");
                    if (format.Description is { Length: > 0 } description)
                    {
                        w.WriteString("description", description);
                    }

                    w.WritePropertyName("schema");
                    WriteRawOrEmptyObject(w, format.SchemaJson);
                    if (format.Strict is { } strict)
                    {
                        w.WriteBoolean("strict", strict);
                    }
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            if (Anthropic.AnthropicThinking.OpenAiEffort(request) is { Length: > 0 } effort)
            {
                w.WriteStartObject("reasoning");
                w.WriteString("effort", effort);
                w.WriteEndObject();
            }

            if (request.Logprobs is { } logprobs && logprobs > 0)
            {
                w.WriteNumber("top_logprobs", logprobs);
            }

            w.WriteBoolean("store", false);
            w.WriteStartArray("include");
            w.WriteStringValue("reasoning.encrypted_content");
            if (request.Logprobs is not null)
            {
                w.WriteStringValue("message.output_text.logprobs");
            }

            w.WriteEndArray();

            if (request.Stream)
            {
                w.WriteBoolean("stream", true);
            }

            if (promptCacheKey is { Length: > 0 })
            {
                w.WriteString("prompt_cache_key", promptCacheKey);
            }

            if (request.EndUserId is { Length: > 0 } user)
            {
                w.WriteString("safety_identifier", user);
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessage(Utf8JsonWriter w, IrMessage message)
    {
        // 一条 IR 消息里的块按顺序切开：普通内容攒成一条 message 项，
        // 工具结果、工具调用、推理各自成项，顺序保持不变（ADR-0005）。
        var pending = new List<IrBlock>();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            w.WriteStartObject();
            w.WriteString("type", "message");
            w.WriteString("role", message.Role == IrRole.Assistant ? "assistant" : "user");
            w.WriteStartArray("content");
            foreach (IrBlock block in pending)
            {
                WritePart(w, block, message.Role);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            pending.Clear();
        }

        foreach (IrBlock block in message.Content)
        {
            switch (block)
            {
                case IrToolResult result:
                    Flush();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call_output");
                    w.WriteString("call_id", result.ToolCallId);
                    WriteToolOutput(w, result);
                    w.WriteEndObject();
                    break;

                case IrToolCall call:
                    Flush();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call");
                    w.WriteString("call_id", call.Id);
                    w.WriteString("name", call.Name);
                    w.WriteString("arguments", call.ArgumentsJson);
                    w.WriteEndObject();
                    break;

                case IrReasoning reasoning when reasoning.SignedFor(ApiFormat.OpenAiResponses):
                    // 只有 Responses 自己产生的推理才能带回去；别家的签名这里不认（Q186）。
                    Flush();
                    w.WriteStartObject();
                    w.WriteString("type", "reasoning");
                    if (reasoning.ItemId is { Length: > 0 } id)
                    {
                        w.WriteString("id", id);
                    }

                    w.WriteStartArray("summary");
                    if (reasoning.Text.Length > 0)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "summary_text");
                        w.WriteString("text", reasoning.Text);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteString("encrypted_content", reasoning.Signature);
                    w.WriteEndObject();
                    break;

                case IrReasoning or IrRedactedReasoning:
                    // 别的格式产生的推理，Responses 不认，丢掉。
                    break;

                case IrText { Text.Length: 0 }:
                    break;

                default:
                    pending.Add(block);
                    break;
            }
        }

        Flush();
    }

    private static void WritePart(Utf8JsonWriter w, IrBlock block, IrRole role)
    {
        switch (block)
        {
            case IrText text when role == IrRole.Assistant:
                w.WriteStartObject();
                if (text.IsRefusal)
                {
                    w.WriteString("type", "refusal");
                    w.WriteString("refusal", text.Text);
                }
                else
                {
                    w.WriteString("type", "output_text");
                    w.WriteString("text", text.Text);
                    w.WriteStartArray("annotations");
                    w.WriteEndArray();
                }

                w.WriteEndObject();
                return;

            case IrText text:
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", text.Text);
                w.WriteEndObject();
                return;

            case IrImage image:
                w.WriteStartObject();
                w.WriteString("type", "input_image");
                w.WriteString("detail", "auto");
                w.WriteString("image_url", image.Url is { } url
                    ? url.ToString()
                    : $"data:{image.MediaType ?? "image/png"};base64,{image.Base64Data}");
                w.WriteEndObject();
                return;

            case IrFile file:
                w.WriteStartObject();
                w.WriteString("type", "input_file");
                if (file.FileName is { Length: > 0 } name)
                {
                    w.WriteString("filename", name);
                }

                w.WriteString("file_data", $"data:{file.MediaType};base64,{file.Base64Data}");
                w.WriteEndObject();
                return;

            default:
                return;
        }
    }

    private static void WriteToolOutput(Utf8JsonWriter w, IrToolResult result)
    {
        // 纯文字结果写成字符串，最通用；带图片才写成内容数组。
        if (result.Content.All(block => block is IrText))
        {
            string text = string.Join("\n", result.Content.Cast<IrText>().Select(t => t.Text));
            w.WriteString("output", result.IsError && text.Length == 0 ? "error" : text);
            return;
        }

        w.WriteStartArray("output");
        foreach (IrBlock block in result.Content)
        {
            WritePart(w, block, IrRole.User);
        }

        w.WriteEndArray();
    }

    private static void WriteRawOrEmptyObject(Utf8JsonWriter w, string? json)
    {
        if (json is { Length: > 0 })
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(json);
                parsed.RootElement.WriteTo(w);
                return;
            }
            catch (JsonException)
            {
                // 坏的 schema 换成空对象，免得整个请求写不出来。
            }
        }

        w.WriteStartObject();
        w.WriteEndObject();
    }
}
