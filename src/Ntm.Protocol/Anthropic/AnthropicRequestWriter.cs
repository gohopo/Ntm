using System.Buffers;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 把 IR 写成 Anthropic Messages 请求体。
/// <c>max_tokens</c> 在 IR 里为空时不写：这个字段 Anthropic 是必填的，
/// 但补默认值的时机在改写规则之后（Q172），由网关那一步决定，不在这里。
/// 空文本块会被丢掉（Anthropic 拒绝 <c>""</c>），丢空之后没有内容的消息也一并跳过。
/// 缓存断点的位置由 <see cref="AnthropicCacheBreakpoints"/> 决定（ADR-0019）。
/// </summary>
public static class AnthropicRequestWriter
{
    /// <summary>
    /// 写出请求体。<paramref name="autoCacheBreakpoints"/> 为真且客户端一个断点都没写时，网关自己加缓存断点；
    /// 客户端写了的断点不管开关都照写。推理强度按 <paramref name="thinkingStyle"/> 换成思考参数（ADR-0021）。
    /// <paramref name="reservedBreakpoints"/> 是之后改写规则会写进去的断点个数（顶层 <c>cache_control</c>
    /// 和规则值里的 <c>cache_control</c>），这里最多放 4 减去它（Q307、Q316）。
    /// </summary>
    public static byte[] Write(
        IrRequest request,
        string upstreamModel,
        bool autoCacheBreakpoints = false,
        AnthropicThinkingStyle thinkingStyle = AnthropicThinkingStyle.Auto,
        int reservedBreakpoints = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        AnthropicCachePlan cache = AnthropicCacheBreakpoints.Plan(
            request,
            autoCacheBreakpoints,
            Math.Max(0, AnthropicCacheBreakpoints.MaxBreakpoints - reservedBreakpoints));
        AnthropicThinkingPlan thinking = AnthropicThinking.Plan(request, thinkingStyle, upstreamModel);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, AnthropicWire.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", upstreamModel);

            if ((thinking.MaxTokens ?? request.MaxOutputTokens) is { } maxTokens)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            WriteSystem(writer, request.System, cache);
            WriteMessages(writer, request.Messages, cache);
            WriteTools(writer, request, cache);

            if (request.Temperature is { } temperature && !thinking.DropTemperature)
            {
                // OpenAI 系的 temperature 到 2，Anthropic 只到 1（Q171）。
                writer.WriteNumber("temperature", Math.Clamp(temperature, 0, 1));
            }

            if (request.TopP is { } topP && !thinking.DropTopP)
            {
                writer.WriteNumber("top_p", topP);
            }

            WriteThinking(writer, thinking);

            if (request.StopSequences.Count > 0)
            {
                writer.WriteStartArray("stop_sequences");
                foreach (string stop in request.StopSequences)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            if (request.Stream)
            {
                writer.WriteBoolean("stream", true);
            }

            if (request.EndUserId is { Length: > 0 } user)
            {
                writer.WriteStartObject("metadata");
                writer.WriteString("user_id", user);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteThinking(Utf8JsonWriter writer, AnthropicThinkingPlan thinking)
    {
        if (thinking.Type is { } type)
        {
            writer.WriteStartObject("thinking");
            writer.WriteString("type", type);
            if (thinking.Budget is { } budget)
            {
                writer.WriteNumber("budget_tokens", budget);
            }

            if (thinking.Display is { } display)
            {
                writer.WriteString("display", display);
            }

            writer.WriteEndObject();
        }

        if (thinking.Effort is { } effort)
        {
            writer.WriteStartObject("output_config");
            writer.WriteString("effort", effort);
            writer.WriteEndObject();
        }
    }

    /// <summary>system 写成块数组：多段 system 保持顺序，不拼成一段。</summary>
    private static void WriteSystem(Utf8JsonWriter writer, IReadOnlyList<string> system, AnthropicCachePlan cache)
    {
        var kept = system.Where(part => part.Length > 0).ToArray();
        if (kept.Length == 0)
        {
            return;
        }

        writer.WriteStartArray("system");
        int slot = cache.SystemBase;
        foreach (string part in kept)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", part);
            WriteCacheControl(writer, cache.At(slot++));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<IrMessage> messages, AnthropicCachePlan cache)
    {
        writer.WriteStartArray("messages");
        int slot = cache.MessageBase;
        foreach (IrMessage message in messages)
        {
            if (!message.Content.Any(AnthropicCacheBreakpoints.Writes))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", message.Role == IrRole.Assistant ? "assistant" : "user");
            writer.WriteStartArray("content");
            foreach (IrBlock block in message.Content)
            {
                if (AnthropicCacheBreakpoints.Writes(block))
                {
                    WriteBlock(writer, block, cache.At(slot++));
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>写 <c>cache_control</c>。5 分钟是默认值，不写 ttl。</summary>
    private static void WriteCacheControl(Utf8JsonWriter writer, IrCacheTtl? ttl)
    {
        if (ttl is not { } value)
        {
            return;
        }

        writer.WriteStartObject("cache_control");
        writer.WriteString("type", "ephemeral");
        if (value == IrCacheTtl.OneHour)
        {
            writer.WriteString("ttl", "1h");
        }

        writer.WriteEndObject();
    }

    private static void WriteBlock(Utf8JsonWriter writer, IrBlock block, IrCacheTtl? cache)
    {
        switch (block)
        {
            case IrText text:
                if (text.Text.Length == 0)
                {
                    return;
                }

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                WriteCacheControl(writer, cache);
                writer.WriteEndObject();
                return;

            case IrImage image:
                WriteImage(writer, image, cache);
                return;

            case IrFile file:
                writer.WriteStartObject();
                writer.WriteString("type", "document");
                writer.WriteStartObject("source");
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", file.MediaType);
                writer.WriteString("data", file.Base64Data);
                writer.WriteEndObject();
                WriteCacheControl(writer, cache);
                writer.WriteEndObject();
                return;

            case IrToolCall call:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", call.Id);
                writer.WriteString("name", call.Name);
                writer.WritePropertyName("input");
                WriteRawJsonOrEmptyObject(writer, call.ArgumentsJson);
                WriteCacheControl(writer, cache);
                writer.WriteEndObject();
                return;

            case IrToolResult result:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", result.ToolCallId);
                if (result.IsError)
                {
                    writer.WriteBoolean("is_error", true);
                }

                writer.WriteStartArray("content");
                foreach (IrBlock inner in result.Content)
                {
                    // 工具结果里只允许文字和图片。
                    switch (inner)
                    {
                        case IrText { Text.Length: > 0 } innerText:
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", innerText.Text);
                            writer.WriteEndObject();
                            break;
                        case IrImage innerImage:
                            WriteImage(writer, innerImage, null);
                            break;
                    }
                }

                writer.WriteEndArray();
                WriteCacheControl(writer, cache);
                writer.WriteEndObject();
                return;

            // 没有签名的思考块回传会被 Anthropic 拒绝，只能丢（ADR-0005）。
            case IrReasoning { Signature: { } signature } reasoning when reasoning.SignedFor(ApiFormat.Anthropic) && reasoning.Text.Length > 0:
                writer.WriteStartObject();
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", reasoning.Text);
                writer.WriteString("signature", signature);
                writer.WriteEndObject();
                return;

            case IrRedactedReasoning redacted:
                writer.WriteStartObject();
                writer.WriteString("type", "redacted_thinking");
                writer.WriteString("data", redacted.Data);
                writer.WriteEndObject();
                return;
        }
    }

    private static void WriteImage(Utf8JsonWriter writer, IrImage image, IrCacheTtl? cache)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteStartObject("source");
        if (image.Url is { } url)
        {
            writer.WriteString("type", "url");
            writer.WriteString("url", url.ToString());
        }
        else
        {
            writer.WriteString("type", "base64");
            writer.WriteString("media_type", image.MediaType ?? "image/png");
            writer.WriteString("data", image.Base64Data ?? string.Empty);
        }

        writer.WriteEndObject();
        WriteCacheControl(writer, cache);
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, IrRequest request, AnthropicCachePlan cache)
    {
        if (request.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            int slot = 0;
            foreach (IrTool tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                if (tool.Description is { Length: > 0 } description)
                {
                    writer.WriteString("description", description);
                }

                writer.WritePropertyName("input_schema");
                WriteRawJsonOrEmptyObject(writer, tool.ParametersSchemaJson);
                WriteCacheControl(writer, cache.At(slot++));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (request.ToolChoice is not { } choice)
        {
            return;
        }

        writer.WriteStartObject("tool_choice");
        switch (choice.Mode)
        {
            case IrToolChoiceMode.None:
                writer.WriteString("type", "none");
                break;
            case IrToolChoiceMode.Any:
                writer.WriteString("type", "any");
                break;
            case IrToolChoiceMode.Specific:
                writer.WriteString("type", "tool");
                writer.WriteString("name", choice.Name ?? string.Empty);
                break;
            default:
                writer.WriteString("type", "auto");
                break;
        }

        if (choice.DisableParallelToolUse && choice.Mode != IrToolChoiceMode.None)
        {
            writer.WriteBoolean("disable_parallel_tool_use", true);
        }

        writer.WriteEndObject();
    }

    /// <summary>原样写入一段 JSON；写不进去就写空对象，免得整个请求成为非法 JSON。</summary>
    private static void WriteRawJsonOrEmptyObject(Utf8JsonWriter writer, string json)
    {
        if (json.Length == 0)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try
        {
            writer.WriteRawValue(json);
        }
        catch (JsonException)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}
