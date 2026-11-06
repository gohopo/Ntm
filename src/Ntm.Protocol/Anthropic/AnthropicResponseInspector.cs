using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>读一次 Anthropic 非流式响应的结果。</summary>
public readonly record struct AnthropicResponseReading
{
    /// <summary>响应体是错误。HTTP 可能仍然是 200。</summary>
    public bool IsError { get; init; }

    /// <summary>解析不了。不据此判定失败——宁可漏判也不要误判。</summary>
    public bool IsMalformed { get; init; }

    /// <summary>有没有真实输出（文字、思考、工具调用）。</summary>
    public bool HasOutput { get; init; }

    /// <summary>见过 thinking 或 redacted_thinking 块没有（Q169）。</summary>
    public bool SawThinking { get; init; }

    /// <summary>上游给出的用量，没拿到就是 <see cref="Usage.Unavailable"/>。</summary>
    public Usage Usage { get; init; }

    /// <summary>上游给出的 <c>stop_reason</c> 原值。</summary>
    public string? FinishReason { get; init; }

    /// <summary>错误原文，用于日志与透给客户端。</summary>
    public string? ErrorPayload { get; init; }

    /// <summary>上游真实返回的模型名。</summary>
    public string? UpstreamModel { get; init; }

    /// <summary>读成 IR 的响应；错误或解析不了时为空。</summary>
    public IrResponse? Response { get; init; }
}

/// <summary>
/// 读 Anthropic Messages 的非流式响应：既做判定（错误、有无输出、用量），
/// 也顺手读成 IR，供转成 OpenAI Chat 用（Q175）。
/// </summary>
public static class AnthropicResponseInspector
{
    public static AnthropicResponseReading Read(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return new AnthropicResponseReading { IsMalformed = true, Usage = Usage.Unavailable };
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AnthropicResponseReading { IsMalformed = true, Usage = Usage.Unavailable };
            }

            if (AnthropicUsageReader.TryReadError(root, out string? error))
            {
                return new AnthropicResponseReading
                {
                    IsError = true,
                    ErrorPayload = error,
                    Usage = Usage.Unavailable,
                };
            }

            string? model = root.TryGetProperty("model", out JsonElement modelNode) && modelNode.ValueKind == JsonValueKind.String
                ? modelNode.GetString()
                : null;

            string? stopReason = root.TryGetProperty("stop_reason", out JsonElement stop) && stop.ValueKind == JsonValueKind.String
                ? stop.GetString()
                : null;

            Usage usage = AnthropicUsageReader.TryRead(root, out Usage read) ? read : Usage.Unavailable;

            List<IrBlock> blocks = [];
            bool sawThinking = false;
            if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? type = block.TryGetProperty("type", out JsonElement typeNode) && typeNode.ValueKind == JsonValueKind.String
                        ? typeNode.GetString()
                        : null;

                    switch (type)
                    {
                        case "text" when Text(block, "text") is { } text:
                            blocks.Add(new IrText(text));
                            break;

                        case "thinking":
                            sawThinking = true;
                            if (Text(block, "thinking") is { } thinking)
                            {
                                blocks.Add(new IrReasoning(thinking, Text(block, "signature"), ApiFormat.Anthropic));
                            }

                            break;

                        case "redacted_thinking":
                            sawThinking = true;
                            if (Text(block, "data") is { } data)
                            {
                                blocks.Add(new IrRedactedReasoning(data));
                            }

                            break;

                        case "tool_use":
                            blocks.Add(new IrToolCall(
                                Text(block, "id") ?? string.Empty,
                                Text(block, "name") ?? string.Empty,
                                block.TryGetProperty("input", out JsonElement input) ? input.GetRawText() : "{}"));
                            break;
                    }
                }
            }

            bool hasOutput = blocks.Count > 0 || stopReason is { Length: > 0 };

            return new AnthropicResponseReading
            {
                HasOutput = hasOutput,
                SawThinking = sawThinking,
                Usage = usage,
                FinishReason = stopReason,
                UpstreamModel = model,
                Response = new IrResponse
                {
                    Model = model,
                    Content = blocks,
                    StopReason = AnthropicStopReason.ToIr(stopReason),
                    Usage = usage,
                },
            };
        }
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Anthropic 的 <c>stop_reason</c> 与 IR 的对应。</summary>
public static class AnthropicStopReason
{
    public static IrStopReason? ToIr(string? stopReason) => stopReason switch
    {
        null or "" => null,
        "max_tokens" => IrStopReason.MaxTokens,
        "stop_sequence" => IrStopReason.StopSequence,
        "tool_use" or "pause_turn" => IrStopReason.ToolUse,
        "refusal" => IrStopReason.Refusal,
        _ => IrStopReason.EndTurn,
    };
}

/// <summary>Anthropic 的用量与错误读取，流式与非流式共用。</summary>
internal static class AnthropicUsageReader
{
    /// <summary>
    /// 把 Anthropic 的 usage 换算成五项互不重叠的桶（ADR-0005）。
    /// <c>input_tokens</c> 本来就不含缓存；两档缓存写入在
    /// <c>cache_creation.ephemeral_5m_input_tokens</c> / <c>ephemeral_1h_input_tokens</c> 里，
    /// 上游没给细分就全算 5 分钟档（Q164）。
    /// </summary>
    public static bool TryRead(JsonElement owner, out Usage usage)
    {
        usage = Usage.Unavailable;
        if (!owner.TryGetProperty("usage", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        long input = ReadLong(node, "input_tokens");
        long output = ReadLong(node, "output_tokens");
        long cacheRead = ReadLong(node, "cache_read_input_tokens");
        long cacheWrite = ReadLong(node, "cache_creation_input_tokens");

        long write5m = cacheWrite;
        long write1h = 0;
        if (node.TryGetProperty("cache_creation", out JsonElement split) && split.ValueKind == JsonValueKind.Object)
        {
            long fine5m = ReadLong(split, "ephemeral_5m_input_tokens");
            long fine1h = ReadLong(split, "ephemeral_1h_input_tokens");
            if (fine5m + fine1h > 0)
            {
                write5m = fine5m;
                write1h = fine1h;
            }
        }

        usage = new Usage
        {
            InputTokens = input,
            CacheReadTokens = cacheRead,
            CacheWrite5mTokens = write5m,
            CacheWrite1hTokens = write1h,
            OutputTokens = output,
            Source = UsageSource.Upstream,
        };
        return true;
    }

    public static bool TryReadError(JsonElement root, out string? payload)
    {
        payload = null;
        bool isErrorType = root.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
            && type.ValueEquals("error");

        if (!root.TryGetProperty("error", out JsonElement error))
        {
            // "type": "error" 但没有 error 字段，也当错误：至少把整体原文带上。
            if (isErrorType)
            {
                payload = root.GetRawText();
                return true;
            }

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
                return isErrorType;
        }
    }

    public static long ReadLong(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : 0;
}
