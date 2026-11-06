using System.Text.Json;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 逐帧读 Anthropic Messages 的流式响应，判定 ContentEvent、提取 Usage、认出终止与软错误。
/// 只读：字节已经在去客户端的路上（ADR-0002）。
/// </summary>
public sealed class AnthropicStreamInspector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>见过第一个 ContentEvent 没有（Q167）。</summary>
    public bool HasContentEvent { get; private set; }

    /// <summary>见过 <c>message_stop</c> 没有。靠终止事件判断流结束，而不是靠连接断开。</summary>
    public bool SawTerminal { get; private set; }

    /// <summary>
    /// 完整用量：只有 <c>message_delta</c> 带了输出 Token 才算拿到。
    /// <c>message_start</c> 里的输入部分放在 <see cref="InputUsage"/>。
    /// </summary>
    public Usage Usage { get; private set; }

    /// <summary>
    /// <c>message_start</c> 给出的输入侧用量。流中途断掉时输出要靠估算，输入用这里的真数（Q177）。
    /// </summary>
    public Usage InputUsage { get; private set; }

    /// <summary>上游给出的 <c>stop_reason</c>。</summary>
    public string? FinishReason { get; private set; }

    /// <summary>软错误的原文，用于日志与透给客户端。</summary>
    public string? ErrorPayload { get; private set; }

    /// <summary>上游真实返回的模型名。</summary>
    public string? UpstreamModel { get; private set; }

    /// <summary>已经看到的输出里的 ASCII 字符数（正文、思考、工具参数增量）。</summary>
    public long OutputAsciiChars { get; private set; }

    /// <summary>已经看到的输出里的非 ASCII 字符数，估算时一字符按一 Token。</summary>
    public long OutputWideChars { get; private set; }

    /// <summary>见过 thinking / redacted_thinking 没有（Q169）。</summary>
    public bool SawThinking { get; private set; }

    /// <summary>流干净结束但一个字都没产出。</summary>
    public bool EndedEmpty => SawTerminal && !HasContentEvent && Usage.OutputTokens == 0;

    /// <summary>读一帧的 <c>data:</c> 内容。Anthropic 的每个数据帧自带 <c>type</c>，所以不看 <c>event:</c> 行。</summary>
    public StreamFrameVerdict Inspect(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return StreamFrameVerdict.Preamble;
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

            string? type = root.TryGetProperty("type", out JsonElement typeNode) && typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()
                : null;

            switch (type)
            {
                case "error":
                    if (AnthropicUsageReader.TryReadError(root, out string? error))
                    {
                        ErrorPayload = error;
                    }

                    return StreamFrameVerdict.Error;

                case "message_start":
                    return InspectMessageStart(root);

                case "content_block_start":
                    return InspectBlockStart(root);

                case "content_block_delta":
                    return InspectBlockDelta(root);

                case "message_delta":
                    return InspectMessageDelta(root);

                case "message_stop":
                    SawTerminal = true;
                    return StreamFrameVerdict.Terminal;

                case "ping" or "content_block_stop":
                    return StreamFrameVerdict.Preamble;

                default:
                    // 认不出的事件按序幕处理：发出去无妨，也不据此判定失败。
                    return StreamFrameVerdict.Preamble;
            }
        }
    }

    private StreamFrameVerdict InspectMessageStart(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object)
        {
            return StreamFrameVerdict.Preamble;
        }

        if (message.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
        {
            UpstreamModel = model.GetString();
        }

        if (AnthropicUsageReader.TryRead(message, out Usage usage))
        {
            InputUsage = usage;
        }

        return StreamFrameVerdict.Preamble;
    }

    private StreamFrameVerdict InspectBlockStart(JsonElement root)
    {
        if (!root.TryGetProperty("content_block", out JsonElement block) || block.ValueKind != JsonValueKind.Object)
        {
            return StreamFrameVerdict.Preamble;
        }

        string? type = block.TryGetProperty("type", out JsonElement typeNode) && typeNode.ValueKind == JsonValueKind.String
            ? typeNode.GetString()
            : null;

        switch (type)
        {
            case "tool_use":
                // 工具调用的 id 和 name 都在这一帧里，客户端必须看到它（Q167）。
                Count(block, "name");
                HasContentEvent = true;
                return StreamFrameVerdict.Content;

            case "redacted_thinking":
                SawThinking = true;
                Count(block, "data");
                HasContentEvent = true;
                return StreamFrameVerdict.Content;

            case "thinking":
                SawThinking = true;
                return StreamFrameVerdict.Preamble;

            default:
                // text 块开始时通常是空的，等 delta 再算内容。
                return StreamFrameVerdict.Preamble;
        }
    }

    private StreamFrameVerdict InspectBlockDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return StreamFrameVerdict.Preamble;
        }

        string? type = delta.TryGetProperty("type", out JsonElement typeNode) && typeNode.ValueKind == JsonValueKind.String
            ? typeNode.GetString()
            : null;

        bool real = type switch
        {
            "text_delta" => Count(delta, "text"),
            "thinking_delta" => CountThinking(delta),
            "input_json_delta" => Count(delta, "partial_json"),
            // signature_delta、citations_delta 不是模型输出的内容。
            _ => false,
        };

        if (!real)
        {
            return StreamFrameVerdict.Preamble;
        }

        HasContentEvent = true;
        return StreamFrameVerdict.Content;
    }

    private bool CountThinking(JsonElement delta)
    {
        bool counted = Count(delta, "thinking");
        if (counted)
        {
            SawThinking = true;
        }

        return counted;
    }

    private StreamFrameVerdict InspectMessageDelta(JsonElement root)
    {
        if (root.TryGetProperty("delta", out JsonElement delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("stop_reason", out JsonElement stop)
            && stop.ValueKind == JsonValueKind.String)
        {
            FinishReason = stop.GetString();
        }

        if (!AnthropicUsageReader.TryRead(root, out Usage usage))
        {
            return StreamFrameVerdict.Preamble;
        }

        // message_delta 的 usage 只给输出侧（有的中转会把输入也带上，带了就用它）。
        Usage merged = usage;
        if (usage.TotalInputTokens == 0 && InputUsage.HasValue)
        {
            merged = usage with
            {
                InputTokens = InputUsage.InputTokens,
                CacheReadTokens = InputUsage.CacheReadTokens,
                CacheWrite5mTokens = InputUsage.CacheWrite5mTokens,
                CacheWrite1hTokens = InputUsage.CacheWrite1hTokens,
            };
        }

        Usage = merged;
        return StreamFrameVerdict.UsageOnly;
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
}
