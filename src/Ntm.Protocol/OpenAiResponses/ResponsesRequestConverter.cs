using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>
/// 把 Responses 请求读成 IR 的结果。三种失败分开：<see cref="Error"/> 是请求本身坏了；
/// <see cref="Unrepresentable"/> 是 IR 装不下、只能交给 Responses 上游透传；
/// <see cref="Rejected"/> 是网关不支持的有状态用法，任何上游都不接，直接 400（Q189、Q196）。
/// </summary>
public readonly record struct ResponsesRequestConversion(IrRequest? Request, string? Error, string? Unrepresentable);

/// <summary>Responses 请求里路由要看的几个字段，不做完整转换（透传只需要这些）。</summary>
public readonly record struct ResponsesRequestReading
{
    public bool IsValid { get; init; }

    public string? Model { get; init; }

    public bool IsStream { get; init; }

    /// <summary>接着上一次响应（ADR-0016）。只能回到产生它的那个渠道和渠道密钥。</summary>
    public string? PreviousResponseId { get; init; }

    /// <summary>接着一个上游对话（Q278）：字符串或 <c>{"id": …}</c> 里的 id。只能回到它所在的渠道和渠道密钥。</summary>
    public string? Conversation { get; init; }

    /// <summary><c>background: true</c>（Q276）：上游先回 queued，结果靠按 id 取回。</summary>
    public bool IsBackground { get; init; }

    /// <summary>网关不支持、任何上游都不接的用法，给出原因直接 400。</summary>
    public string? Rejected { get; init; }

    /// <summary>instructions 原文和第一条非 system 输入，给 Conversation 的身份用（Q118）。</summary>
    public string? ConversationSeed { get; init; }
}

/// <summary>读 OpenAI Responses 的请求：路由字段，以及按需完整读成 IR（只有要转给别的格式时才做）。</summary>
public static class ResponsesRequestConverter
{
    /// <summary>只读路由要看的字段，并认出网关拒收的用法。</summary>
    public static ResponsesRequestReading Read(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), ResponsesWire.DocumentOptions);
        }
        catch (JsonException)
        {
            return default;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ResponsesWire.String(root, "model") is not { Length: > 0 } model)
            {
                return default;
            }

            return new ResponsesRequestReading
            {
                IsValid = true,
                Model = model,
                IsStream = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True,
                PreviousResponseId = ResponsesWire.String(root, "previous_response_id") is { Length: > 0 } previous ? previous : null,
                Conversation = ConversationId(root),
                IsBackground = root.TryGetProperty("background", out JsonElement background) && background.ValueKind == JsonValueKind.True,
                Rejected = Rejected(root),
                ConversationSeed = ConversationSeed(root),
            };
        }
    }

    /// <summary>完整读成 IR。</summary>
    public static ResponsesRequestConversion ToIr(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), ResponsesWire.DocumentOptions);
        }
        catch (JsonException)
        {
            return new ResponsesRequestConversion(null, "请求体不是合法的 JSON。", null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ResponsesRequestConversion(null, "请求体必须是 JSON 对象。", null);
            }

            if (ResponsesWire.String(root, "model") is not { Length: > 0 } model)
            {
                return new ResponsesRequestConversion(null, "缺少 model。", null);
            }

            if (Unrepresentable(root) is { } why)
            {
                return new ResponsesRequestConversion(null, null, why);
            }

            var system = new List<string>();
            if (ResponsesWire.String(root, "instructions") is { Length: > 0 } instructions)
            {
                system.Add(instructions);
            }

            var messages = new List<IrMessage>();
            if (ReadInput(root, system, messages) is { } inputProblem)
            {
                return new ResponsesRequestConversion(null, null, inputProblem);
            }

            (IReadOnlyList<IrTool> tools, string? toolProblem) = ReadTools(root);
            if (toolProblem is not null)
            {
                return new ResponsesRequestConversion(null, null, toolProblem);
            }

            (IrToolChoice? toolChoice, string? choiceProblem) = ReadToolChoice(root);
            if (choiceProblem is not null)
            {
                return new ResponsesRequestConversion(null, null, choiceProblem);
            }

            var request = new IrRequest
            {
                Model = model,
                System = system,
                Messages = messages,
                Tools = tools,
                ToolChoice = toolChoice,
                MaxOutputTokens = root.TryGetProperty("max_output_tokens", out JsonElement max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt64(out long maxTokens)
                    ? maxTokens
                    : null,
                Temperature = Number(root, "temperature"),
                TopP = Number(root, "top_p"),
                Stream = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True,
                EndUserId = ResponsesWire.String(root, "safety_identifier") ?? ResponsesWire.String(root, "user"),
                ResponseFormat = ReadResponseFormat(root),
                Logprobs = ReadLogprobs(root),
                ReasoningEffort = root.TryGetProperty("reasoning", out JsonElement reasoning)
                    ? ResponsesWire.String(reasoning, "effort")
                    : null,
            };

            return new ResponsesRequestConversion(request, null, null);
        }
    }

    /// <summary>
    /// 网关拒收（Q278/Q281）：conversation 写得不对、和 previous_response_id 一起用（上游也不许），
    /// 或者 item_reference 既没有 previous_response_id 也没有 conversation——那样网关不知道它引用的项存在哪个账号里。
    /// </summary>
    private static string? Rejected(JsonElement root)
    {
        bool hasConversation = root.TryGetProperty("conversation", out JsonElement conversation)
            && conversation.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (hasConversation && ConversationId(root) is null)
        {
            return "conversation 要写成对话 id 字符串，或者 {\"id\": \"conv_…\"}。";
        }

        bool hasPrevious = ResponsesWire.String(root, "previous_response_id") is { Length: > 0 };
        if (hasConversation && hasPrevious)
        {
            return "conversation 和 previous_response_id 只能用一个。";
        }

        if (!hasConversation && !hasPrevious && HasItemReference(root))
        {
            return "item_reference 引用的是上游存着的输出项，网关只有在请求同时带着 previous_response_id 或 conversation 时才知道它存在哪个渠道。"
                + "请带上其中一个，或者把那一项的完整内容放进 input。";
        }

        return null;
    }

    private static string? ConversationId(JsonElement root)
    {
        if (!root.TryGetProperty("conversation", out JsonElement conversation))
        {
            return null;
        }

        string? id = conversation.ValueKind switch
        {
            JsonValueKind.String => conversation.GetString(),
            JsonValueKind.Object => ResponsesWire.String(conversation, "id"),
            _ => null,
        };
        return id is { Length: > 0 } ? id : null;
    }

    private static bool HasItemReference(JsonElement root)
    {
        if (root.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in input.EnumerateArray())
            {
                if (ResponsesWire.String(item, "type") == "item_reference")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>IR 装不下、只能透传给 Responses 上游的用法（ADR-0014）。</summary>
    private static string? Unrepresentable(JsonElement root)
    {
        if (ResponsesWire.String(root, "previous_response_id") is { Length: > 0 })
        {
            return "请求带了 previous_response_id，只有产生那次响应的 Responses 渠道接得了。";
        }

        if (ConversationId(root) is not null)
        {
            return "请求带了 conversation，只有那个上游对话所在的 Responses 渠道接得了。";
        }

        if (root.TryGetProperty("background", out JsonElement background) && background.ValueKind == JsonValueKind.True)
        {
            return "请求开了 background，结果要按 id 到产出它的上游取回，只有 Responses 上游接得了。";
        }

        if (HasItemReference(root))
        {
            return "请求用了 item_reference，只有 Responses 上游接得了。";
        }

        if (root.TryGetProperty("prompt", out JsonElement prompt) && prompt.ValueKind == JsonValueKind.Object)
        {
            return "请求用了存在上游的 prompt 模板，只有 Responses 上游接得了。";
        }

        return null;
    }

    private static string? ReadInput(JsonElement root, List<string> system, List<IrMessage> messages)
    {
        if (!root.TryGetProperty("input", out JsonElement input))
        {
            return null;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            Append(messages, IrRole.User, new IrText(input.GetString() ?? string.Empty));
            return null;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement item in input.EnumerateArray())
        {
            string? type = ResponsesWire.String(item, "type");
            switch (type)
            {
                case null or "message":
                    {
                        string? role = ResponsesWire.String(item, "role");
                        if (role is "system" or "developer")
                        {
                            string text = PlainText(item);
                            if (text.Length > 0)
                            {
                                system.Add(text);
                            }

                            continue;
                        }

                        IrRole irRole = role == "assistant" ? IrRole.Assistant : IrRole.User;
                        if (ReadContent(item, irRole, out List<IrBlock> blocks) is { } problem)
                        {
                            return problem;
                        }

                        foreach (IrBlock block in blocks)
                        {
                            Append(messages, irRole, block);
                        }

                        continue;
                    }

                case "function_call":
                    Append(messages, IrRole.Assistant, new IrToolCall(
                        ResponsesWire.String(item, "call_id") ?? string.Empty,
                        ResponsesWire.String(item, "name") ?? string.Empty,
                        ResponsesWire.String(item, "arguments") is { Length: > 0 } arguments ? arguments : "{}"));
                    continue;

                case "function_call_output":
                    {
                        var output = new List<IrBlock>();
                        if (item.TryGetProperty("output", out JsonElement value))
                        {
                            if (value.ValueKind == JsonValueKind.String)
                            {
                                output.Add(new IrText(value.GetString() ?? string.Empty));
                            }
                            else if (value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement part in value.EnumerateArray())
                                {
                                    if (ReadPart(part, out IrBlock? block) is { } problem)
                                    {
                                        return problem;
                                    }

                                    if (block is not null)
                                    {
                                        output.Add(block);
                                    }
                                }
                            }
                        }

                        Append(messages, IrRole.User, new IrToolResult(ResponsesWire.String(item, "call_id") ?? string.Empty, output, false));
                        continue;
                    }

                case "reasoning":
                    Append(messages, IrRole.Assistant, ResponsesResponseInspector.ReadReasoning(item));
                    continue;

                default:
                    return $"输入里有 {type} 类型的项，只有 Responses 上游接得了。";
            }
        }

        return null;
    }

    private static string? ReadContent(JsonElement item, IrRole role, out List<IrBlock> blocks)
    {
        blocks = [];
        if (!item.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            blocks.Add(new IrText(content.GetString() ?? string.Empty));
            return null;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement part in content.EnumerateArray())
        {
            if (ReadPart(part, out IrBlock? block) is { } problem)
            {
                return problem;
            }

            if (block is not null)
            {
                blocks.Add(block);
            }
        }

        _ = role;
        return null;
    }

    private static string? ReadPart(JsonElement part, out IrBlock? block)
    {
        block = null;
        switch (ResponsesWire.String(part, "type"))
        {
            case "input_text" or "output_text" or "text":
                block = new IrText(ResponsesWire.String(part, "text") ?? string.Empty);
                return null;

            case "refusal":
                block = new IrText(ResponsesWire.String(part, "refusal") ?? string.Empty, IsRefusal: true);
                return null;

            case "input_image":
                {
                    string? url = ResponsesWire.String(part, "image_url");
                    if (url is null)
                    {
                        return "输入里的图片用的是 file_id，只有 Responses 上游取得到。";
                    }

                    if (TryDataUrl(url, out string? mediaType, out string? data))
                    {
                        block = new IrImage(mediaType, data, null);
                        return null;
                    }

                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
                    {
                        block = new IrImage(null, null, uri);
                        return null;
                    }

                    return "输入里的图片地址既不是 data: 也不是 http(s)。";
                }

            case "input_file":
                {
                    if (ResponsesWire.String(part, "file_data") is { Length: > 0 } fileData
                        && TryDataUrl(fileData, out string? mediaType, out string? data))
                    {
                        block = new IrFile(mediaType ?? "application/pdf", data!, ResponsesWire.String(part, "filename"));
                        return null;
                    }

                    return "输入里的文件用的是 file_id 或外链，只有 Responses 上游取得到。";
                }

            default:
                return $"输入里有 {ResponsesWire.String(part, "type")} 类型的内容，只有 Responses 上游接得了。";
        }
    }

    private static (IReadOnlyList<IrTool> Tools, string? Problem) ReadTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out JsonElement tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return ([], null);
        }

        var result = new List<IrTool>();
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string? type = ResponsesWire.String(tool, "type");
            if (type != "function")
            {
                return ([], $"请求用了 {type} 工具，只有 Responses 上游接得了。");
            }

            result.Add(new IrTool(
                ResponsesWire.String(tool, "name") ?? string.Empty,
                ResponsesWire.String(tool, "description"),
                tool.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object
                    ? parameters.GetRawText()
                    : "{\"type\":\"object\"}"));
        }

        return (result, null);
    }

    private static (IrToolChoice? Choice, string? Problem) ReadToolChoice(JsonElement root)
    {
        bool disableParallel = root.TryGetProperty("parallel_tool_calls", out JsonElement parallel) && parallel.ValueKind == JsonValueKind.False;
        if (!root.TryGetProperty("tool_choice", out JsonElement choice) || choice.ValueKind == JsonValueKind.Null)
        {
            return (disableParallel ? new IrToolChoice(IrToolChoiceMode.Auto, null, true) : null, null);
        }

        if (choice.ValueKind == JsonValueKind.String)
        {
            IrToolChoiceMode mode = choice.GetString() switch
            {
                "none" => IrToolChoiceMode.None,
                "required" => IrToolChoiceMode.Any,
                _ => IrToolChoiceMode.Auto,
            };
            return (new IrToolChoice(mode, null, disableParallel), null);
        }

        if (choice.ValueKind == JsonValueKind.Object && ResponsesWire.String(choice, "type") == "function")
        {
            return (new IrToolChoice(IrToolChoiceMode.Specific, ResponsesWire.String(choice, "name"), disableParallel), null);
        }

        return (null, "请求的 tool_choice 指定了内置工具或工具子集，只有 Responses 上游接得了。");
    }

    private static IrResponseFormat? ReadResponseFormat(JsonElement root)
    {
        if (!root.TryGetProperty("text", out JsonElement text)
            || !text.TryGetProperty("format", out JsonElement format)
            || format.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ResponsesWire.String(format, "type") switch
        {
            "json_object" => new IrResponseFormat(IrResponseFormatKind.JsonObject, null, null, null, null),
            "json_schema" => new IrResponseFormat(
                IrResponseFormatKind.JsonSchema,
                ResponsesWire.String(format, "name"),
                ResponsesWire.String(format, "description"),
                format.TryGetProperty("schema", out JsonElement schema) ? schema.GetRawText() : null,
                format.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? strict.GetBoolean()
                    : null),
            _ => null,
        };
    }

    /// <summary>Responses 用 <c>top_logprobs</c> 加 include 里的 <c>message.output_text.logprobs</c> 要 logprobs（Q188）。</summary>
    private static int? ReadLogprobs(JsonElement root)
    {
        bool included = false;
        if (root.TryGetProperty("include", out JsonElement include) && include.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in include.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() == "message.output_text.logprobs")
                {
                    included = true;
                }
            }
        }

        int? top = ResponsesWire.Int(root, "top_logprobs");
        return included || top > 0 ? top ?? 0 : null;
    }

    /// <summary>Conversation 身份的种子：instructions、开头的 system/developer 输入，加第一条其他输入的原文（Q118）。</summary>
    private static string? ConversationSeed(JsonElement root)
    {
        var seed = new System.Text.StringBuilder();
        if (ResponsesWire.String(root, "instructions") is { Length: > 0 } instructions)
        {
            seed.Append(instructions).Append('\n');
        }

        if (!root.TryGetProperty("input", out JsonElement input))
        {
            return null;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            return seed.Append(input.GetString()).ToString();
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement item in input.EnumerateArray())
        {
            seed.Append(item.GetRawText()).Append('\n');
            if (ResponsesWire.String(item, "role") is not ("system" or "developer"))
            {
                return seed.ToString();
            }
        }

        return null;
    }

    private static void Append(List<IrMessage> messages, IrRole role, IrBlock block)
    {
        if (messages.Count > 0 && messages[^1].Role == role && messages[^1].Content is List<IrBlock> content)
        {
            content.Add(block);
            return;
        }

        messages.Add(new IrMessage(role, new List<IrBlock> { block }));
    }

    private static string PlainText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        var text = new System.Text.StringBuilder();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (ResponsesWire.String(part, "text") is { Length: > 0 } piece)
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(piece);
                }
            }
        }

        return text.ToString();
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool TryDataUrl(string url, out string? mediaType, out string? data)
    {
        mediaType = null;
        data = null;
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }

        string header = url[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mediaType = header[..^7] is { Length: > 0 } type ? type : null;
        data = url[(comma + 1)..];
        return true;
    }
}
