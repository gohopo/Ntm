using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 解析结果：要么是 IR，要么是一句可以直接回给客户端的错误。
/// <see cref="AnthropicOnly"/> 不为空时，请求合法、可以透传给 Anthropic 上游，
/// 但带着只有 Anthropic 自己执行得了的东西（服务端工具及其历史块），转换到别的格式要绕开（Q218、ADR-0014）。
/// </summary>
public readonly record struct AnthropicRequestReading(IrRequest? Request, string? Error, string? AnthropicOnly = null)
{
    public bool IsValid => Request is not null;
}

/// <summary>
/// 把 Anthropic Messages 请求解析成 IR。
/// 认不出的内容块直接拒绝，不静默丢弃——丢掉用户内容比返回 400 更糟。
/// </summary>
public static class AnthropicRequestReader
{
    public const int DefaultMaxBodyBytes = 32 * 1024 * 1024;

    public static AnthropicRequestReading Read(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Length > DefaultMaxBodyBytes)
        {
            return Fail("请求体为空或过大。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Fail("请求体不是合法的 JSON。");
        }

        using (document)
        {
            try
            {
                var state = new ReadState();
                IrRequest request = ReadRequest(document.RootElement, state);
                return new AnthropicRequestReading(request, null, state.AnthropicOnly);
            }
            catch (FormatException ex)
            {
                // 只有 Invalid() 抛 FormatException：数字和 URL 都用 TryXxx 读。
                return Fail(ex.Message);
            }
            catch (InvalidOperationException)
            {
                // 某个字段的 JSON 类型不对（比如数组位置给了字符串）。
                return Fail("请求结构不符合 Anthropic Messages 格式。");
            }
        }
    }

    private static AnthropicRequestReading Fail(string error) => new(null, error);

    private static IrRequest ReadRequest(JsonElement root, ReadState state)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("请求体必须是 JSON 对象。");
        }

        string model = OptionalString(root, "model") is { Length: > 0 } m ? m : throw Invalid("缺少 model。");

        if (!root.TryGetProperty("messages", out JsonElement messagesElement)
            || messagesElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("缺少 messages。");
        }

        var messages = new List<IrMessage>(messagesElement.GetArrayLength());
        foreach (JsonElement message in messagesElement.EnumerateArray())
        {
            messages.Add(ReadMessage(message, state));
        }

        (string? effort, long? budget, bool disabled) = ReadThinking(root);

        return new IrRequest
        {
            Model = model,
            System = ReadSystem(root),
            Messages = messages,
            Tools = ReadTools(root, state),
            ToolChoice = ReadToolChoice(root),
            MaxOutputTokens = OptionalInt64(root, "max_tokens"),
            Temperature = OptionalDouble(root, "temperature"),
            TopP = OptionalDouble(root, "top_p"),
            StopSequences = ReadStringArray(root, "stop_sequences"),
            Stream = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True,
            EndUserId = root.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object
                ? OptionalString(metadata, "user_id")
                : null,
            ReasoningEffort = effort,
            ThinkingBudget = budget,
            ThinkingDisabled = disabled,
        };
    }

    /// <summary>
    /// 读 <c>thinking</c> 和 <c>output_config.effort</c>（ADR-0021）：enabled 记预算，adaptive 只看 effort，
    /// disabled 和 between_tools 记为关闭。两样都有时 effort 优先，预算也留着。
    /// 形状不对的不报错，当作没写：这些只是提示，原样转发时上游自己会判。
    /// </summary>
    private static (string? Effort, long? Budget, bool Disabled) ReadThinking(JsonElement root)
    {
        string? effort = root.TryGetProperty("output_config", out JsonElement output) && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("effort", out JsonElement e) && e.ValueKind == JsonValueKind.String
            && e.GetString() is { Length: > 0 } text
                ? text
                : null;

        if (!root.TryGetProperty("thinking", out JsonElement thinking) || thinking.ValueKind != JsonValueKind.Object
            || !thinking.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
        {
            return (effort, null, false);
        }

        switch (type.GetString())
        {
            case "enabled":
                long? budget = thinking.TryGetProperty("budget_tokens", out JsonElement b) && b.ValueKind == JsonValueKind.Number
                    && b.TryGetInt64(out long value) && value > 0
                        ? value
                        : null;
                return (effort, budget, false);
            case "disabled":
            case "between_tools":
                return (effort, null, true);
            default:
                return (effort, null, false);
        }
    }

    private static List<string> ReadSystem(JsonElement root)
    {
        var system = new List<string>();
        if (!root.TryGetProperty("system", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return system;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            system.Add(element.GetString()!);
            return system;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("system 必须是字符串或文本块数组。");
        }

        foreach (JsonElement block in element.EnumerateArray())
        {
            if (TypeOf(block) != "text")
            {
                throw Invalid("system 只接受 text 块。");
            }

            system.Add(RequiredString(block, "text"));
        }

        return system;
    }

    private static IrMessage ReadMessage(JsonElement message, ReadState state)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("messages 的每一项必须是对象。");
        }

        IrRole role = OptionalString(message, "role") switch
        {
            "user" => IrRole.User,
            "assistant" => IrRole.Assistant,
            _ => throw Invalid("message.role 只能是 user 或 assistant。"),
        };

        if (!message.TryGetProperty("content", out JsonElement content))
        {
            throw Invalid("message 缺少 content。");
        }

        return new IrMessage(role, ReadContent(content, state));
    }

    private static List<IrBlock> ReadContent(JsonElement content, ReadState state)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return [new IrText(content.GetString()!)];
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("content 必须是字符串或内容块数组。");
        }

        var blocks = new List<IrBlock>(content.GetArrayLength());
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (ReadBlock(block, state) is { } read)
            {
                blocks.Add(read);
            }
        }

        return blocks;
    }

    private static IrBlock? ReadBlock(JsonElement block, ReadState state)
    {
        string type = TypeOf(block);
        if (IsServerToolBlock(type))
        {
            // 服务端工具的调用和结果只有 Anthropic 认得：透传照发，转换时绕开（Q218）。
            state.AnthropicOnly ??= $"历史里有 Anthropic 服务端工具的内容块（{type}），这个上游格式没有对应的位置。";
            return null;
        }

        return type switch
        {
            "text" => new IrText(RequiredString(block, "text")),
            "image" => ReadImage(block),
            "document" => ReadDocument(block),
            "tool_use" => new IrToolCall(
                RequiredString(block, "id"),
                RequiredString(block, "name"),
                block.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object
                    ? input.GetRawText()
                    : "{}"),
            "tool_result" => ReadToolResult(block, state),
            "thinking" => new IrReasoning(OptionalString(block, "thinking") ?? string.Empty, OptionalString(block, "signature"), ApiFormat.Anthropic),
            "redacted_thinking" => new IrRedactedReasoning(RequiredString(block, "data")),
            _ => throw Invalid($"不支持的内容块类型：{type}。"),
        };
    }

    private static IrImage ReadImage(JsonElement block)
    {
        JsonElement source = RequiredObject(block, "source");
        switch (OptionalString(source, "type"))
        {
            case "base64":
                return new IrImage(RequiredString(source, "media_type"), RequiredString(source, "data"), null);
            case "url":
                return Uri.TryCreate(RequiredString(source, "url"), UriKind.Absolute, out Uri? url)
                    ? new IrImage(null, null, url)
                    : throw Invalid("image.source.url 不是合法的绝对地址。");
            default:
                throw Invalid("image.source.type 只支持 base64 和 url。");
        }
    }

    private static IrBlock ReadDocument(JsonElement block)
    {
        JsonElement source = RequiredObject(block, "source");
        return OptionalString(source, "type") switch
        {
            "base64" => new IrFile(RequiredString(source, "media_type"), RequiredString(source, "data"), OptionalString(block, "title")),
            "text" => new IrText(RequiredString(source, "data")),
            _ => throw Invalid("document.source.type 只支持 base64 和 text。"),
        };
    }

    private static IrToolResult ReadToolResult(JsonElement block, ReadState state)
    {
        string toolCallId = RequiredString(block, "tool_use_id");
        bool isError = block.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True;

        List<IrBlock> content =
            !block.TryGetProperty("content", out JsonElement element) || element.ValueKind == JsonValueKind.Null
                ? []
                : ReadContent(element, state);

        if (content.Exists(b => b is not (IrText or IrImage or IrFile)))
        {
            throw Invalid("tool_result.content 只接受 text、image 和 document 块。");
        }

        return new IrToolResult(toolCallId, content, isError);
    }

    private static List<IrTool> ReadTools(JsonElement root, ReadState state)
    {
        var tools = new List<IrTool>();
        if (!root.TryGetProperty("tools", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return tools;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("tools 必须是数组。");
        }

        foreach (JsonElement tool in element.EnumerateArray())
        {
            // 服务端工具（web_search 等）没有 input_schema，由 Anthropic 自己执行；
            // 转到别的上游后没人能执行它。搜不搜会实质改变结果，所以绕开别的格式，不悄悄丢掉（Q218、ADR-0014）。
            if (!tool.TryGetProperty("input_schema", out JsonElement schema))
            {
                string name = OptionalString(tool, "type") ?? OptionalString(tool, "name") ?? "未知";
                state.AnthropicOnly ??= $"请求带着 Anthropic 服务端工具（{name}），这个上游格式执行不了。";
                continue;
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("tool.input_schema 必须是对象。");
            }

            tools.Add(new IrTool(RequiredString(tool, "name"), OptionalString(tool, "description"), schema.GetRawText()));
        }

        return tools;
    }

    private static IrToolChoice? ReadToolChoice(JsonElement root)
    {
        if (!root.TryGetProperty("tool_choice", out JsonElement choice) || choice.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (choice.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("tool_choice 必须是对象。");
        }

        bool disableParallel = choice.TryGetProperty("disable_parallel_tool_use", out JsonElement d)
            && d.ValueKind == JsonValueKind.True;

        return OptionalString(choice, "type") switch
        {
            "auto" => new IrToolChoice(IrToolChoiceMode.Auto, null, disableParallel),
            "any" => new IrToolChoice(IrToolChoiceMode.Any, null, disableParallel),
            "none" => new IrToolChoice(IrToolChoiceMode.None, null, disableParallel),
            "tool" => new IrToolChoice(IrToolChoiceMode.Specific, RequiredString(choice, "name"), disableParallel),
            _ => throw Invalid("tool_choice.type 无法识别。"),
        };
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var values = new List<string>();
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return values;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{name} 必须是字符串数组。");
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            values.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid($"{name} 必须是字符串数组。"));
        }

        return values;
    }

    private static long? OptionalInt64(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : throw Invalid($"{name} 必须是整数。");
    }

    private static double? OptionalDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result)
            ? result
            : throw Invalid($"{name} 必须是数字。");
    }

    private static string TypeOf(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object
            ? OptionalString(block, "type") ?? throw Invalid("内容块缺少 type。")
            : throw Invalid("内容块必须是对象。");

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredString(JsonElement obj, string name) =>
        OptionalString(obj, name) ?? throw Invalid($"缺少字段 {name}。");

    private static JsonElement RequiredObject(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw Invalid($"缺少对象 {name}。");

    /// <summary>服务端工具的调用块和结果块：server_tool_use、mcp_tool_use 以及 web_search_tool_result 之类的 *_tool_result。</summary>
    private static bool IsServerToolBlock(string type) =>
        type is "server_tool_use" or "mcp_tool_use" or "container_upload"
        || (type.EndsWith("_tool_result", StringComparison.Ordinal) && type != "tool_result");

    private static FormatException Invalid(string message) => new(message);

    private sealed class ReadState
    {
        public string? AnthropicOnly { get; set; }
    }
}
