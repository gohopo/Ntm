using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 对 OpenAI Chat 请求体做定点字节改写（ADR-0002）：原始字节照搬，只替换要改的那一小段，
/// 绝不从 IR 重新序列化——那会丢掉这个格式特有的字段。
/// </summary>
public static class OpenAiChatRequestPatcher
{
    private static ReadOnlySpan<byte> IncludeUsageObject => "{\"include_usage\":true}"u8;

    private static ReadOnlySpan<byte> IncludeUsageMember => "\"stream_options\":{\"include_usage\":true}"u8;

    /// <summary>
    /// 保证请求体里有 <c>stream_options.include_usage = true</c>。
    /// 流式 OpenAI Chat 不加这个就拿不到 usage，而我们要计费。
    /// 客户端自己没要 usage 时，多出来的那个块由转发方吞掉。
    /// </summary>
    /// <returns>改写了就返回 true 并给出新字节；本来就有或改不了就返回 false。</returns>
    public static bool TryEnsureIncludeUsage(ReadOnlySpan<byte> body, out byte[] patched)
    {
        patched = [];

        if (!TryLocate(body, out int valueStart, out int valueEnd, out int insertAt, out bool hasMembers))
        {
            return false;
        }

        if (valueStart >= 0)
        {
            // stream_options 已经在了：只换它的值，别塞重复键靠「后者覆盖」——那是解析器的实现细节。
            int length = body.Length - (valueEnd - valueStart) + IncludeUsageObject.Length;
            patched = new byte[length];
            Span<byte> target = patched;
            body[..valueStart].CopyTo(target);
            IncludeUsageObject.CopyTo(target[valueStart..]);
            body[valueEnd..].CopyTo(target[(valueStart + IncludeUsageObject.Length)..]);
            return true;
        }

        int extra = IncludeUsageMember.Length + (hasMembers ? 1 : 0);
        patched = new byte[body.Length + extra];
        Span<byte> output = patched;
        body[..insertAt].CopyTo(output);
        int written = insertAt;
        if (hasMembers)
        {
            output[written++] = (byte)',';
        }

        IncludeUsageMember.CopyTo(output[written..]);
        written += IncludeUsageMember.Length;
        body[insertAt..].CopyTo(output[written..]);
        return true;
    }

    /// <summary>
    /// 把顶层 <c>model</c> 的值换成上游模型名。客户端说的是 PublishedModel，上游认的是 UpstreamModel，
    /// 两者不同时只换这一个值，其余字节原样（ADR-0002）。
    /// </summary>
    /// <returns>换了就返回 true；名字本来就一样、没有 model 或不是合法 JSON 时返回 false。</returns>
    public static bool TryReplaceModel(ReadOnlySpan<byte> body, string model, out byte[] patched)
    {
        ArgumentNullException.ThrowIfNull(model);
        patched = [];

        if (!TryLocateTopLevelString(body, "model"u8, out int valueStart, out int valueEnd, out string? current)
            || string.Equals(current, model, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(model);
        patched = new byte[body.Length - (valueEnd - valueStart) + encoded.Length];
        Span<byte> target = patched;
        body[..valueStart].CopyTo(target);
        encoded.CopyTo(target[valueStart..]);
        body[valueEnd..].CopyTo(target[(valueStart + encoded.Length)..]);
        return true;
    }

    private static bool TryLocateTopLevelString(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> name,
        out int valueStart,
        out int valueEnd,
        out string? value)
    {
        valueStart = -1;
        valueEnd = -1;
        value = null;

        if (body.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    return false;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                bool matches = reader.ValueTextEquals(name);
                if (!reader.Read())
                {
                    return false;
                }

                if (matches)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    valueStart = (int)reader.TokenStartIndex;
                    valueEnd = (int)reader.BytesConsumed;
                    value = reader.GetString();
                    return true;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// 找到顶层 <c>stream_options</c> 的值区间，或者顶层对象收尾 <c>}</c> 的位置。
    /// </summary>
    private static bool TryLocate(
        ReadOnlySpan<byte> body,
        out int valueStart,
        out int valueEnd,
        out int insertAt,
        out bool hasMembers)
    {
        valueStart = -1;
        valueEnd = -1;
        insertAt = -1;
        hasMembers = false;

        if (body.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    insertAt = (int)reader.TokenStartIndex;
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                hasMembers = true;
                bool isStreamOptions = reader.ValueTextEquals("stream_options"u8);

                if (!reader.Read())
                {
                    return false;
                }

                if (isStreamOptions)
                {
                    valueStart = (int)reader.TokenStartIndex;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }

                    valueEnd = (int)reader.BytesConsumed;
                    return true;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
