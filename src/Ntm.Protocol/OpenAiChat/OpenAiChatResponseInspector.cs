using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>非流式响应的读取结果。</summary>
public readonly record struct OpenAiChatResponseReading
{
    /// <summary>响应体是错误。HTTP 可能仍然是 200。</summary>
    public bool IsError { get; init; }

    /// <summary>解析不了。不据此判定失败。</summary>
    public bool IsMalformed { get; init; }

    /// <summary>有没有产出内容：至少一个 choice 的 message 里有文本、推理、工具调用或 refusal。</summary>
    public bool HasOutput { get; init; }

    /// <summary>
    /// 输出里有没有非空的思考内容（<c>reasoning_content</c> 或 <c>reasoning</c>）。
    /// 思考模式的计价按它判定，而不是按可选的 <c>reasoning_tokens</c>（ADR-0013）。
    /// </summary>
    public bool SawThinking { get; init; }

    public Usage Usage { get; init; }

    public string? FinishReason { get; init; }

    public string? ErrorPayload { get; init; }

    public string? UpstreamModel { get; init; }
}

/// <summary>
/// 读 OpenAI Chat 的非流式响应。非流式整体缓冲，发出前不写任何字节，所以它天然原子：
/// 要么整体成功，要么还能换候选（ADR-0008）。
/// 整体解析一次，而不是像参考实现（ccLoad）那样只探前 512 字节——
/// 那样大于 512 字节的 200 错误体会当成成功漏过去。
/// </summary>
public static class OpenAiChatResponseInspector
{
    /// <summary>缓冲上限。超过就不解析，按成功透传，只是拿不到用量。</summary>
    public const int DefaultMaxBodyBytes = 32 * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static OpenAiChatResponseReading Read(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return new OpenAiChatResponseReading { IsMalformed = true };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), DocumentOptions);
        }
        catch (JsonException)
        {
            return new OpenAiChatResponseReading { IsMalformed = true };
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OpenAiChatResponseReading { IsMalformed = true };
            }

            if (TryReadError(root, out string? error))
            {
                return new OpenAiChatResponseReading { IsError = true, ErrorPayload = error };
            }

            string? model = root.TryGetProperty("model", out JsonElement modelNode) && modelNode.ValueKind == JsonValueKind.String
                ? modelNode.GetString()
                : null;

            string? finishReason = null;
            bool hasOutput = false;
            bool sawThinking = false;

            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                    {
                        finishReason = finish.GetString();
                    }

                    if (choice.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object
                        && HasRealOutput(message))
                    {
                        hasOutput = true;
                        sawThinking |= HasThinking(message);
                    }
                }
            }

            return new OpenAiChatResponseReading
            {
                HasOutput = hasOutput,
                SawThinking = sawThinking,
                Usage = TryReadUsage(root, out Usage usage) ? usage : Usage.Unavailable,
                FinishReason = finishReason,
                UpstreamModel = model,
            };
        }
    }

    private static bool HasThinking(JsonElement message) =>
        HasNonEmptyString(message, "reasoning_content") || HasNonEmptyString(message, "reasoning");

    private static bool HasRealOutput(JsonElement message)
    {
        if (HasNonEmptyString(message, "content")
            || HasNonEmptyString(message, "reasoning_content")
            || HasNonEmptyString(message, "reasoning")
            || HasNonEmptyString(message, "refusal"))
        {
            return true;
        }

        return message.TryGetProperty("tool_calls", out JsonElement toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array
            && toolCalls.GetArrayLength() > 0;
    }

    private static bool HasNonEmptyString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 };

    private static bool TryReadError(JsonElement root, out string? payload)
    {
        payload = null;
        if (!root.TryGetProperty("error", out JsonElement error))
        {
            return false;
        }

        switch (error.ValueKind)
        {
            case JsonValueKind.Object when error.EnumerateObject().Any():
                payload = error.GetRawText();
                return true;
            case JsonValueKind.String when error.GetString() is { Length: > 0 } text:
                payload = text;
                return true;
            default:
                return false;
        }
    }

    internal static bool TryReadUsage(JsonElement root, out Usage usage)
    {
        usage = Usage.Unavailable;
        if (!root.TryGetProperty("usage", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        long prompt = ReadLong(node, "prompt_tokens");
        long completion = ReadLong(node, "completion_tokens");

        long cacheRead = 0;
        if (node.TryGetProperty("prompt_tokens_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object)
        {
            cacheRead = ReadLong(details, "cached_tokens");
        }

        long hit = ReadLong(node, "prompt_cache_hit_tokens");
        if (hit > 0)
        {
            cacheRead = hit;
        }

        usage = new Usage
        {
            InputTokens = Math.Max(0, prompt - cacheRead),
            CacheReadTokens = cacheRead,
            CacheWrite5mTokens = 0,
            CacheWrite1hTokens = 0,
            OutputTokens = completion,
            Source = UsageSource.Upstream,
        };
        return true;
    }

    private static long ReadLong(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : 0;
}
