using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.OpenAiResponses;

/// <summary>OpenAI Responses 各读写器共用的小件：用量换算、错误读取、停止原因、JSON 选项。</summary>
internal static class ResponsesWire
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Responses 的 <c>input_tokens</c> 含缓存读和缓存写，换成五项互不重叠的桶（ADR-0005、Q190）：
    /// 缓存读 = <c>cached_tokens</c>，缓存写按 5 分钟档 = <c>cache_write_tokens</c>，其余是未命中缓存的输入。
    /// <paramref name="owner"/> 是带 <c>usage</c> 字段的对象（整个响应）。
    /// </summary>
    public static bool TryReadUsage(JsonElement owner, out Usage usage, out long reasoningTokens)
    {
        usage = Usage.Unavailable;
        reasoningTokens = 0;
        if (!owner.TryGetProperty("usage", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        long input = Long(node, "input_tokens");
        long output = Long(node, "output_tokens");
        long cached = 0;
        long cacheWrite = 0;
        if (node.TryGetProperty("input_tokens_details", out JsonElement inputDetails) && inputDetails.ValueKind == JsonValueKind.Object)
        {
            cached = Long(inputDetails, "cached_tokens");
            cacheWrite = Long(inputDetails, "cache_write_tokens");
        }

        if (node.TryGetProperty("output_tokens_details", out JsonElement outputDetails) && outputDetails.ValueKind == JsonValueKind.Object)
        {
            reasoningTokens = Long(outputDetails, "reasoning_tokens");
        }

        usage = new Usage
        {
            InputTokens = Math.Max(0, input - cached - cacheWrite),
            CacheReadTokens = cached,
            CacheWrite5mTokens = cacheWrite,
            OutputTokens = output,
            Source = UsageSource.Upstream,
        };
        return true;
    }

    /// <summary>五项桶写回 Responses 的 <c>usage</c>：<c>input_tokens</c> 重新把缓存加进去。</summary>
    public static void WriteUsage(Utf8JsonWriter writer, Usage usage)
    {
        long cacheWrite = usage.CacheWrite5mTokens + usage.CacheWrite1hTokens;
        writer.WriteStartObject("usage");
        writer.WriteNumber("input_tokens", usage.TotalInputTokens);
        writer.WriteStartObject("input_tokens_details");
        writer.WriteNumber("cached_tokens", usage.CacheReadTokens);
        if (cacheWrite > 0)
        {
            writer.WriteNumber("cache_write_tokens", cacheWrite);
        }

        writer.WriteEndObject();
        writer.WriteNumber("output_tokens", usage.OutputTokens);
        writer.WriteStartObject("output_tokens_details");
        writer.WriteNumber("reasoning_tokens", 0);
        writer.WriteEndObject();
        writer.WriteNumber("total_tokens", usage.TotalInputTokens + usage.OutputTokens);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 认出错误：顶层 <c>{"error":{…}}</c>（HTTP 错误体）、流里的 <c>{"type":"error",…}</c>
    /// （规范写平铺，SDK 测试里是嵌套的，两种都认，Q197），或者 <c>status:"failed"</c> 的响应。
    /// </summary>
    public static bool TryReadError(JsonElement root, out string? payload)
    {
        payload = null;
        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            payload = error.GetRawText();
            return true;
        }

        if (String(root, "type") == "error")
        {
            payload = root.GetRawText();
            return true;
        }

        if (String(root, "status") == "failed")
        {
            payload = root.TryGetProperty("error", out JsonElement failed) && failed.ValueKind != JsonValueKind.Null
                ? failed.GetRawText()
                : "{\"message\":\"upstream response failed\"}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 响应的状态换成 IR 停止原因。Responses 没有 <c>stop_reason</c>：
    /// <c>incomplete</c> + <c>max_output_tokens</c> 是截断，<c>content_filter</c> 按拒绝，
    /// 正常结束时有工具调用就是要调工具。
    /// </summary>
    public static IrStopReason? StopReason(JsonElement response, bool hasToolCalls)
    {
        string? status = String(response, "status");
        if (status == "incomplete")
        {
            string? reason = response.TryGetProperty("incomplete_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object
                    ? String(details, "reason")
                    : null;
            return reason == "content_filter" ? IrStopReason.Refusal : IrStopReason.MaxTokens;
        }

        if (status is "completed" or null)
        {
            return hasToolCalls ? IrStopReason.ToolUse : IrStopReason.EndTurn;
        }

        return null;
    }

    /// <summary>IR 停止原因写回 Responses 的 status 与 <c>incomplete_details.reason</c>。</summary>
    public static (string Status, string? IncompleteReason) Status(IrStopReason? reason) => reason switch
    {
        IrStopReason.MaxTokens => ("incomplete", "max_output_tokens"),
        _ => ("completed", null),
    };

    public static string? String(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long Long(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? Math.Max(0, number)
            : 0;

    public static int? Int(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : null;

    /// <summary>推理项里有没有可见的推理：summary / content 里有字，或者带了加密内容（Q191）。</summary>
    public static bool HasReasoning(JsonElement item)
    {
        if (String(item, "encrypted_content") is { Length: > 0 })
        {
            return true;
        }

        foreach (string field in (ReadOnlySpan<string>)["summary", "content"])
        {
            if (item.TryGetProperty(field, out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (String(part, "text") is { Length: > 0 })
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
