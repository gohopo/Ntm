using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>一帧在流里的角色。</summary>
public enum StreamFrameVerdict
{
    /// <summary>序幕：只有 role 的增量、空增量、心跳。发出去也不妨碍换候选（ADR-0008）。</summary>
    Preamble = 0,

    /// <summary>携带模型真实输出。第一个这样的帧写给客户端即为 Commit。</summary>
    Content = 1,

    /// <summary>只带用量，没有输出。OpenAI Chat 在 <c>include_usage</c> 下的最后一个数据帧。</summary>
    UsageOnly = 2,

    /// <summary>终止标记 <c>[DONE]</c>。</summary>
    Terminal = 3,

    /// <summary>响应体是错误。HTTP 可能仍然是 200。</summary>
    Error = 4,

    /// <summary>解析不了。不据此判定失败——宁可漏判也不要误判。</summary>
    Malformed = 5,
}

/// <summary>
/// 逐帧读 OpenAI Chat 的流式响应，判定 ContentEvent、提取 Usage、认出终止与软错误。
/// 只读：字节已经在去客户端的路上（ADR-0002）。
/// </summary>
public sealed class OpenAiChatStreamInspector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>见过第一个 ContentEvent 没有。</summary>
    public bool HasContentEvent { get; private set; }

    /// <summary>见过 <c>[DONE]</c> 没有。靠终止事件判断流结束，而不是靠连接断开。</summary>
    public bool SawTerminal { get; private set; }

    /// <summary>上游给出的用量，没拿到就是 <see cref="Usage.Unavailable"/>。</summary>
    public Usage Usage { get; private set; }

    /// <summary>上游给出的结束原因，例如 <c>stop</c>、<c>length</c>、<c>tool_calls</c>、<c>content_filter</c>。</summary>
    public string? FinishReason { get; private set; }

    /// <summary>软错误的原文，用于日志与透给客户端。</summary>
    public string? ErrorPayload { get; private set; }

    /// <summary>上游真实返回的模型名。响应里的 <c>model</c> 不改写（ADR-0002）。</summary>
    public string? UpstreamModel { get; private set; }

    /// <summary>
    /// 已经看到的输出里的 ASCII 字符数：正文、推理、拒答与工具调用参数的增量。
    /// 客户端断开时上游不会再给 usage，和 <see cref="OutputWideChars"/> 一起用来估算输出 Token（Q47）。
    /// </summary>
    public long OutputAsciiChars { get; private set; }

    /// <summary>已经看到的输出里的非 ASCII 字符数（中日韩等），估算时一字符按一 Token。</summary>
    public long OutputWideChars { get; private set; }

    /// <summary>
    /// 见过非空的思考增量（<c>reasoning_content</c> 或 <c>reasoning</c>）没有。
    /// 思考模式的计价按它判定（ADR-0013）；断开后用估算用量时也照样有效。
    /// </summary>
    public bool SawThinking { get; private set; }

    /// <summary>
    /// 流干净结束但一个字都没产出。这不是成功：转给客户端是个空流，
    /// 换一个候选可能有结果。用量上有输出 token 时不算，那说明判定漏了而不是真没产出。
    /// </summary>
    public bool EndedEmpty => SawTerminal && !HasContentEvent && Usage.OutputTokens == 0;

    /// <summary>读一帧的 <c>data:</c> 内容。</summary>
    public StreamFrameVerdict Inspect(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return StreamFrameVerdict.Preamble;
        }

        if (data.SequenceEqual("[DONE]"u8))
        {
            SawTerminal = true;
            return StreamFrameVerdict.Terminal;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data.ToArray(), DocumentOptions);
        }
        catch (JsonException)
        {
            return StreamFrameVerdict.Malformed;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return StreamFrameVerdict.Malformed;
            }

            if (TryReadError(root, out string? error))
            {
                ErrorPayload = error;
                return StreamFrameVerdict.Error;
            }

            if (root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
            {
                UpstreamModel = model.GetString();
            }

            bool hasUsage = TryReadUsage(root, out Usage usage);
            if (hasUsage)
            {
                Usage = usage;
            }

            bool isContent = HasRealOutput(root);
            if (isContent)
            {
                HasContentEvent = true;
                return StreamFrameVerdict.Content;
            }

            return hasUsage ? StreamFrameVerdict.UsageOnly : StreamFrameVerdict.Preamble;
        }
    }

    private bool HasRealOutput(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool real = false;
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // finish_reason 非空就是真实输出：模型已经给出结论，客户端必须看到。
            // 首帧就 content_filter 也算——重试只会再被同一个过滤器拦住。
            if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
            {
                FinishReason = finish.GetString();
                real = true;
            }

            if (!choice.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool thinking = Count(delta, "reasoning_content") | Count(delta, "reasoning");
            if (thinking)
            {
                SawThinking = true;
            }

            bool text = Count(delta, "content")
                | thinking
                | Count(delta, "refusal");
            if (text)
            {
                real = true;
            }

            if (delta.TryGetProperty("tool_calls", out JsonElement toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array
                && toolCalls.GetArrayLength() > 0)
            {
                real = true;
                foreach (JsonElement call in toolCalls.EnumerateArray())
                {
                    if (call.ValueKind == JsonValueKind.Object
                        && call.TryGetProperty("function", out JsonElement function)
                        && function.ValueKind == JsonValueKind.Object)
                    {
                        Count(function, "name");
                        Count(function, "arguments");
                    }
                }
            }
        }

        return real;
    }

    /// <summary>把这个字符串字段的字符计入输出；字段不存在、不是字符串或为空时返回 false。</summary>
    private bool Count(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.IsAscii(c))
            {
                OutputAsciiChars++;
            }
            else
            {
                OutputWideChars++;
            }
        }

        return true;
    }

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
                // "error": null / {} / "" 是上游的噪声，不是错误。
                return false;
        }
    }

    /// <summary>
    /// 把 OpenAI Chat 的 usage 换算成五项互不重叠的桶（ADR-0005）。
    /// <c>prompt_tokens</c> 含缓存读，所以非缓存输入要减掉它。
    /// </summary>
    private static bool TryReadUsage(JsonElement root, out Usage usage)
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

        // DeepSeek 把命中与未命中分开给，不走 prompt_tokens_details。
        long hit = ReadLong(node, "prompt_cache_hit_tokens");
        if (hit > 0)
        {
            cacheRead = hit;
        }

        // 上游偶尔给出不自洽的数（缓存读大于总输入），钳一下而不是记负数。
        long input = Math.Max(0, prompt - cacheRead);

        usage = new Usage
        {
            InputTokens = input,
            CacheReadTokens = cacheRead,
            // OpenAI Chat 的缓存是自动的，没有写入计费项，所以这两项在这个格式下恒为 0。
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
