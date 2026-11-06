using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 读 OpenAI Chat 请求的结果。三种情形：
/// 读成了 IR；请求本身不合法（<see cref="Error"/>）；
/// 合法但有别的格式表达不了的参数（<see cref="Unrepresentable"/>，ADR-0014）。
/// </summary>
public readonly record struct OpenAiChatRequestConversion(IrRequest? Request, string? Error, string? Unrepresentable);

/// <summary>
/// 把 OpenAI Chat 请求读成 IR（Q171）。只在候选里真有非 OpenAI 格式的渠道时才调用：
/// 纯 OpenAI 直通的路径上不需要读整个请求（ADR-0014）。
/// </summary>
public static class OpenAiChatRequestConverter
{
    public static OpenAiChatRequestConversion ToIr(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return new OpenAiChatRequestConversion(null, "请求体不是合法的 JSON。", null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OpenAiChatRequestConversion(null, "请求体要是一个 JSON 对象。", null);
            }

            if (Unrepresentable(root) is { } reason)
            {
                return new OpenAiChatRequestConversion(null, null, reason);
            }

            if (!root.TryGetProperty("model", out JsonElement modelNode)
                || modelNode.ValueKind != JsonValueKind.String
                || modelNode.GetString() is not { Length: > 0 } model)
            {
                return new OpenAiChatRequestConversion(null, "请求里没有 model。", null);
            }

            if (!root.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
            {
                return new OpenAiChatRequestConversion(null, "请求里没有 messages。", null);
            }

            List<string> system = [];
            List<IrCacheTtl?> systemCache = [];
            List<IrMessage> turns = [];
            List<IrBlock>? pendingToolResults = null;

            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string role = Text(message, "role") ?? string.Empty;

                if (role is "tool")
                {
                    // 连续的 tool 消息要合成同一条 user 消息（Q171）：
                    // Anthropic 要求一轮里的 tool_result 挨在一起。
                    string id = Text(message, "tool_call_id") ?? string.Empty;
                    List<IrBlock> inner = [];
                    if (ReadContent(message, inner) is { } contentError)
                    {
                        return new OpenAiChatRequestConversion(null, null, contentError);
                    }

                    pendingToolResults ??= [];

                    // 写在 tool 消息上的断点落到对应的 tool_result 块上（Q242）。
                    pendingToolResults.Add(new IrToolResult(id, inner, false)
                    {
                        Cache = inner.LastOrDefault(b => b.Cache is not null)?.Cache,
                    });
                    continue;
                }

                if (pendingToolResults is { Count: > 0 })
                {
                    turns.Add(new IrMessage(IrRole.User, pendingToolResults));
                    pendingToolResults = null;
                }

                switch (role)
                {
                    case "system" or "developer":
                        {
                            List<IrBlock> blocks = [];
                            if (ReadContent(message, blocks) is { } error)
                            {
                                return new OpenAiChatRequestConversion(null, null, error);
                            }

                            foreach (IrBlock block in blocks)
                            {
                                if (block is IrText { Text.Length: > 0 } text)
                                {
                                    system.Add(text.Text);
                                    systemCache.Add(text.Cache);
                                }
                            }

                            break;
                        }

                    case "user":
                        {
                            List<IrBlock> blocks = [];
                            if (ReadContent(message, blocks) is { } error)
                            {
                                return new OpenAiChatRequestConversion(null, null, error);
                            }

                            turns.Add(new IrMessage(IrRole.User, blocks));
                            break;
                        }

                    case "assistant":
                        {
                            List<IrBlock> blocks = [];
                            if (ReadContent(message, blocks) is { } error)
                            {
                                return new OpenAiChatRequestConversion(null, null, error);
                            }

                            // reasoning_content 不回传：没有签名的思考块 Anthropic 会拒（ADR-0005）。
                            if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement call in calls.EnumerateArray())
                                {
                                    if (call.ValueKind != JsonValueKind.Object
                                        || !call.TryGetProperty("function", out JsonElement function)
                                        || function.ValueKind != JsonValueKind.Object)
                                    {
                                        continue;
                                    }

                                    string arguments = Text(function, "arguments") ?? string.Empty;
                                    if (arguments.Length == 0)
                                    {
                                        arguments = "{}";
                                    }
                                    else if (!IsJson(arguments))
                                    {
                                        return new OpenAiChatRequestConversion(
                                            null,
                                            null,
                                            "历史里的工具调用参数不是合法的 JSON，转不成这个上游格式要的形状。");
                                    }

                                    blocks.Add(new IrToolCall(
                                        Text(call, "id") ?? string.Empty,
                                        Text(function, "name") ?? string.Empty,
                                        arguments));
                                }
                            }

                            turns.Add(new IrMessage(IrRole.Assistant, blocks));
                            break;
                        }

                    case "function":
                        return new OpenAiChatRequestConversion(null, null, "历史里有已废弃的 function 角色消息，这个上游格式没有对应的位置。");
                }
            }

            if (pendingToolResults is { Count: > 0 })
            {
                turns.Add(new IrMessage(IrRole.User, pendingToolResults));
            }

            if (ReadTools(root, out List<IrTool> tools) is { } toolError)
            {
                return new OpenAiChatRequestConversion(null, null, toolError);
            }

            var request = new IrRequest
            {
                Model = model,
                System = system,
                SystemCache = systemCache.Any(c => c is not null) ? systemCache : [],
                Messages = turns,
                Tools = tools,
                ToolChoice = ReadToolChoice(root),
                MaxOutputTokens = ReadMaxTokens(root),
                Temperature = ReadTemperature(root),
                TopP = ReadDouble(root, "top_p"),
                StopSequences = ReadStop(root),
                Stream = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True,
                EndUserId = Text(root, "user") ?? Text(root, "safety_identifier"),
                ResponseFormat = ReadResponseFormat(root),
                Logprobs = ReadLogprobs(root),
                ReasoningEffort = Text(root, "reasoning_effort"),
            };

            return new OpenAiChatRequestConversion(request, null, null);
        }
    }

    /// <summary>
    /// 会改变结果形状、IR 里没有位置的参数（Q173/ADR-0014）。
    /// logprobs 和 response_format 在 IR 里有位置，由 <see cref="IrTargetCheck"/> 按上游格式判断。
    /// 只影响采样细节的参数（seed、frequency_penalty、presence_penalty、logit_bias、service_tier、store）不在此列，转换时静悄悄丢掉。
    /// </summary>
    private static string? Unrepresentable(JsonElement root)
    {
        if (root.TryGetProperty("n", out JsonElement n)
            && n.ValueKind == JsonValueKind.Number
            && n.TryGetInt32(out int count)
            && count > 1)
        {
            return "请求要 n>1 条回答，这个上游格式只能给一条。";
        }

        if (root.TryGetProperty("audio", out JsonElement audio) && audio.ValueKind == JsonValueKind.Object)
        {
            return "请求要音频输出，这个上游格式不支持。";
        }

        if (root.TryGetProperty("modalities", out JsonElement modalities) && modalities.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement modality in modalities.EnumerateArray())
            {
                if (modality.ValueKind == JsonValueKind.String && !modality.ValueEquals("text"))
                {
                    return "请求要文字以外的输出形式，这个上游格式不支持。";
                }
            }
        }

        if (root.TryGetProperty("functions", out JsonElement functions) && functions.ValueKind == JsonValueKind.Array)
        {
            return "请求用的是已废弃的 functions 参数，这个上游格式没有对应的位置。";
        }

        // 内置搜索由上游自己执行，转到别的格式后没人执行它（Q218）。
        if (root.TryGetProperty("web_search_options", out JsonElement search) && search.ValueKind == JsonValueKind.Object)
        {
            return "请求开了内置搜索（web_search_options），这个上游格式不支持。";
        }

        return null;
    }

    /// <summary><c>response_format</c>：text 或缺省为空；json_object、json_schema 进 IR。</summary>
    private static IrResponseFormat? ReadResponseFormat(JsonElement root)
    {
        if (!root.TryGetProperty("response_format", out JsonElement format) || format.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        switch (Text(format, "type"))
        {
            case "json_object":
                return new IrResponseFormat(IrResponseFormatKind.JsonObject, null, null, null, null);
            case "json_schema":
                format.TryGetProperty("json_schema", out JsonElement schema);
                bool isObject = schema.ValueKind == JsonValueKind.Object;
                return new IrResponseFormat(
                    IrResponseFormatKind.JsonSchema,
                    isObject ? Text(schema, "name") : null,
                    isObject ? Text(schema, "description") : null,
                    isObject && schema.TryGetProperty("schema", out JsonElement body) ? body.GetRawText() : null,
                    isObject && schema.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? strict.GetBoolean()
                        : null);
            default:
                return null;
        }
    }

    /// <summary><c>logprobs: true</c> 加可选的 <c>top_logprobs</c>；只给 top_logprobs 也算要。</summary>
    private static int? ReadLogprobs(JsonElement root)
    {
        bool wants = root.TryGetProperty("logprobs", out JsonElement logprobs) && logprobs.ValueKind == JsonValueKind.True;
        int? top = root.TryGetProperty("top_logprobs", out JsonElement topLogprobs)
            && topLogprobs.ValueKind == JsonValueKind.Number
            && topLogprobs.TryGetInt32(out int n)
                ? Math.Max(0, n)
                : null;
        return wants || top is not null ? top ?? 0 : null;
    }

    /// <summary>
    /// 读一条消息的 content。返回非空表示这段内容表达不了。
    /// 客户端写的缓存断点（OpenRouter 写在 content part 上，也有写在消息上的）记到块上，
    /// 只有转去 Anthropic 时写出来（Q242）。
    /// </summary>
    private static string? ReadContent(JsonElement message, List<IrBlock> blocks)
    {
        int before = blocks.Count;
        string? error = ReadContentCore(message, blocks);
        if (error is null && blocks.Count > before && ReadCache(message) is { } cache && blocks[^1].Cache is null)
        {
            blocks[^1] = blocks[^1] with { Cache = cache };
        }

        return error;
    }

    /// <summary>读 <c>cache_control: {"type": "ephemeral", "ttl"?: "5m" | "1h"}</c>。</summary>
    private static IrCacheTtl? ReadCache(JsonElement owner) =>
        owner.TryGetProperty("cache_control", out JsonElement node)
        && node.ValueKind == JsonValueKind.Object
        && Text(node, "type") == "ephemeral"
            ? Text(node, "ttl") == "1h" ? IrCacheTtl.OneHour : IrCacheTtl.FiveMinutes
            : null;

    private static string? ReadContentCore(JsonElement message, List<IrBlock> blocks)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                if (content.GetString() is { Length: > 0 } text)
                {
                    blocks.Add(new IrText(text));
                }

                return null;

            case JsonValueKind.Null:
                return null;

            case JsonValueKind.Array:
                foreach (JsonElement part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    int before = blocks.Count;
                    if (ReadContentPart(part, blocks) is { } error)
                    {
                        return error;
                    }

                    if (blocks.Count > before && ReadCache(part) is { } cache)
                    {
                        blocks[^1] = blocks[^1] with { Cache = cache };
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static string? ReadContentPart(JsonElement part, List<IrBlock> blocks)
    {
        switch (Text(part, "type"))
        {
            case "text" or "input_text":
                if (Text(part, "text") is { Length: > 0 } text)
                {
                    blocks.Add(new IrText(text));
                }

                return null;

            case "image_url":
                {
                    if (!part.TryGetProperty("image_url", out JsonElement image) || image.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    if (Text(image, "url") is not { Length: > 0 } url)
                    {
                        return null;
                    }

                    if (TryReadDataUrl(url, out string? mediaType, out string? data))
                    {
                        blocks.Add(new IrImage(mediaType, data, null));
                        return null;
                    }

                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute)
                        && absolute.Scheme is "http" or "https")
                    {
                        blocks.Add(new IrImage(null, null, absolute));
                        return null;
                    }

                    return "消息里的图片地址既不是 data: 也不是 http(s)，转不过去。";
                }

            case "input_audio":
                return "消息里带音频输入，这个上游格式不支持。";

            case "file":
                {
                    if (!part.TryGetProperty("file", out JsonElement file) || file.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    if (Text(file, "file_data") is { Length: > 0 } fileData
                        && TryReadDataUrl(fileData, out string? mediaType, out string? data))
                    {
                        blocks.Add(new IrFile(mediaType ?? "application/pdf", data!, Text(file, "filename")));
                        return null;
                    }

                    return "消息里的文件只给了 file_id 或不是 data: 形式，这个上游格式取不到它。";
                }

            default:
                return null;
        }
    }

    /// <summary>解析 <c>data:&lt;media type&gt;;base64,&lt;data&gt;</c>。</summary>
    private static bool TryReadDataUrl(string value, out string? mediaType, out string? data)
    {
        mediaType = null;
        data = null;
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            return false;
        }

        int comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }

        string head = value[5..comma];
        if (!head.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mediaType = head[..^";base64".Length];
        data = value[(comma + 1)..];
        return mediaType.Length > 0 && data.Length > 0;
    }

    private static string? ReadTools(JsonElement root, out List<IrTool> tools)
    {
        tools = [];
        if (!root.TryGetProperty("tools", out JsonElement node) || node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement tool in node.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = Text(tool, "type");
            if (type is not null and not "function")
            {
                return $"请求里有 type={type} 的工具，这个上游格式只认函数工具。";
            }

            if (!tool.TryGetProperty("function", out JsonElement function) || function.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(function, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            string schema = function.TryGetProperty("parameters", out JsonElement parameters)
                && parameters.ValueKind == JsonValueKind.Object
                ? parameters.GetRawText()
                : """{"type":"object"}""";

            tools.Add(new IrTool(name, Text(function, "description"), schema)
            {
                Cache = ReadCache(tool) ?? ReadCache(function),
            });
        }

        return null;
    }

    private static IrToolChoice? ReadToolChoice(JsonElement root)
    {
        bool disableParallel = root.TryGetProperty("parallel_tool_calls", out JsonElement parallel)
            && parallel.ValueKind == JsonValueKind.False;

        if (!root.TryGetProperty("tool_choice", out JsonElement choice))
        {
            return disableParallel ? new IrToolChoice(IrToolChoiceMode.Auto, null, true) : null;
        }

        switch (choice.ValueKind)
        {
            case JsonValueKind.String:
                return choice.GetString() switch
                {
                    "none" => new IrToolChoice(IrToolChoiceMode.None, null, disableParallel),
                    "required" => new IrToolChoice(IrToolChoiceMode.Any, null, disableParallel),
                    _ => new IrToolChoice(IrToolChoiceMode.Auto, null, disableParallel),
                };

            case JsonValueKind.Object:
                {
                    string? name = choice.TryGetProperty("function", out JsonElement function)
                        && function.ValueKind == JsonValueKind.Object
                        ? Text(function, "name")
                        : null;

                    return name is { Length: > 0 }
                        ? new IrToolChoice(IrToolChoiceMode.Specific, name, disableParallel)
                        : new IrToolChoice(IrToolChoiceMode.Auto, null, disableParallel);
                }

            default:
                return disableParallel ? new IrToolChoice(IrToolChoiceMode.Auto, null, true) : null;
        }
    }

    /// <summary><c>max_completion_tokens</c> 是新名字，优先于 <c>max_tokens</c>。</summary>
    private static long? ReadMaxTokens(JsonElement root)
    {
        if (root.TryGetProperty("max_completion_tokens", out JsonElement modern)
            && modern.ValueKind == JsonValueKind.Number
            && modern.TryGetInt64(out long newer))
        {
            return newer;
        }

        if (root.TryGetProperty("max_tokens", out JsonElement legacy)
            && legacy.ValueKind == JsonValueKind.Number
            && legacy.TryGetInt64(out long older))
        {
            return older;
        }

        return null;
    }

    /// <summary>原样读进 IR；Anthropic 只到 1，钳制放在 Anthropic 写出器里（Q171），转给 Responses 时不受影响。</summary>
    private static double? ReadTemperature(JsonElement root) =>
        ReadDouble(root, "temperature") is { } temperature ? temperature : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double result)
            ? result
            : null;

    private static List<string> ReadStop(JsonElement root)
    {
        if (!root.TryGetProperty("stop", out JsonElement stop))
        {
            return [];
        }

        switch (stop.ValueKind)
        {
            case JsonValueKind.String when stop.GetString() is { Length: > 0 } single:
                return [single];
            case JsonValueKind.Array:
                {
                    List<string> values = [];
                    foreach (JsonElement item in stop.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        {
                            values.Add(text);
                        }
                    }

                    return values;
                }

            default:
                return [];
        }
    }

    private static bool IsJson(string value)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
