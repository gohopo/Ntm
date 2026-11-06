using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ntm.Protocol;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 认出一个 Conversation（Q118）：API 密钥 + PublishedModel + 系统提示 + 第一条非系统消息的 SHA-256。
/// 同一段对话的后续请求开头这几样不变，换话题就是新 Conversation。
/// 用客户端发来的原始 JSON 文本，不重新序列化：客户端每次都原样发，原样比较最便宜也最不会认错。
/// </summary>
public static class ConversationKey
{
    /// <summary>算不出来（不是 JSON、没有消息）时返回 null，这个 Request 就不参与 Affinity。</summary>
    public static UInt128? Compute(ApiFormat inboundFormat, ReadOnlySpan<byte> body, int? apiKeyId, string publishedModel)
    {
        ArgumentNullException.ThrowIfNull(publishedModel);

        try
        {
            var reader = new Utf8JsonReader(body);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Responses 把对话放在 input（字符串或项的数组），系统提示还可以写在 instructions。
            string listName = inboundFormat == ApiFormat.OpenAiResponses ? "input" : "messages";
            if (!root.TryGetProperty(listName, out JsonElement messages)
                || messages.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
            {
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(number, apiKeyId ?? 0);
            hash.AppendData(number);
            Append(hash, publishedModel);

            if (inboundFormat == ApiFormat.Anthropic && root.TryGetProperty("system", out JsonElement system))
            {
                Append(hash, system.GetRawText());
            }

            if (inboundFormat == ApiFormat.OpenAiResponses && root.TryGetProperty("instructions", out JsonElement instructions))
            {
                Append(hash, instructions.GetRawText());
            }

            bool found = false;
            if (messages.ValueKind == JsonValueKind.String)
            {
                // input 只是一段文字：它本身就是第一条消息。
                Append(hash, messages.GetRawText());
                found = messages.GetString() is { Length: > 0 };
            }

            if (messages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (IsSystem(message))
                    {
                        Append(hash, message.GetRawText());
                        continue;
                    }

                    Append(hash, message.GetRawText());
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return null;
            }

            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            return BinaryPrimitives.ReadUInt128LittleEndian(digest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSystem(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("role", out JsonElement role)
        && role.ValueKind == JsonValueKind.String
        && (role.ValueEquals("system") || role.ValueEquals("developer"));

    /// <summary>每段带长度前缀，免得两段拼起来和另一种切法撞上。</summary>
    private static void Append(IncrementalHash hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
