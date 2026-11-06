using System.Text.Json;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 对 Anthropic Messages 请求体做定点字节改写。直通路径上原始字节照搬，
/// 只补上这个格式缺不得的东西（ADR-0002）。
/// </summary>
public static class AnthropicRequestPatcher
{
    /// <summary>
    /// 保证请求体里有顶层 <c>max_tokens</c>。Anthropic 把它当必填，
    /// 而 OpenAI Chat 的客户端经常不写。补默认值要排在改写规则之后（Q172）：
    /// 管理员用一条「补充 max_tokens」的规则就能换成自己的值，这里只兜底。
    /// </summary>
    /// <returns>补了就返回 true 并给出新字节；本来就有或不是合法 JSON 对象时返回 false。</returns>
    public static bool TryEnsureMaxTokens(ReadOnlySpan<byte> body, long value, out byte[] patched)
    {
        patched = [];

        if (!TryLocateInsertPoint(body, "max_tokens"u8, out int insertAt, out bool hasMembers, out bool present)
            || present)
        {
            return false;
        }

        byte[] member = System.Text.Encoding.UTF8.GetBytes($"\"max_tokens\":{value}");
        int extra = member.Length + (hasMembers ? 1 : 0);
        patched = new byte[body.Length + extra];
        Span<byte> output = patched;
        body[..insertAt].CopyTo(output);
        int written = insertAt;
        if (hasMembers)
        {
            output[written++] = (byte)',';
        }

        member.CopyTo(output[written..]);
        written += member.Length;
        body[insertAt..].CopyTo(output[written..]);
        return true;
    }

    /// <summary>找顶层对象收尾 <c>}</c> 的位置，同时看这个键在不在。</summary>
    private static bool TryLocateInsertPoint(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> name,
        out int insertAt,
        out bool hasMembers,
        out bool present)
    {
        insertAt = -1;
        hasMembers = false;
        present = false;

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
                if (reader.ValueTextEquals(name))
                {
                    present = true;
                }

                if (!reader.Read())
                {
                    return false;
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
