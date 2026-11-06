using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>从 OpenAI Chat 请求体里读选路要用的那几个字段。只读，不重建。</summary>
public readonly record struct OpenAiChatRequestReading
{
    /// <summary>请求体是不是一个能解析的 JSON 对象。</summary>
    public bool IsValid { get; init; }

    /// <summary>客户端要的模型名。</summary>
    public string? Model { get; init; }

    /// <summary><c>stream</c> 是否为 true。</summary>
    public bool IsStream { get; init; }

    /// <summary>客户端有没有自己写 <c>stream_options</c>。</summary>
    public bool HasStreamOptions { get; init; }

    /// <summary>客户端自己要不要 usage。没写 <c>stream_options</c> 时是 false。</summary>
    public bool ClientWantsUsage { get; init; }
}

/// <summary>
/// 解析 OpenAI Chat 请求体里的选路字段（ADR-0002：只解析选路要用的，其余字节原样转发）。
/// </summary>
public static class OpenAiChatRequestReader
{
    /// <summary>请求体上限。超过直接判为非法，不去解析。</summary>
    public const int DefaultMaxBodyBytes = 32 * 1024 * 1024;

    public static OpenAiChatRequestReading Read(
        ReadOnlySpan<byte> body,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        if (body.IsEmpty || body.Length > maxBodyBytes)
        {
            return default;
        }

        string? model = null;
        bool isStream = false;
        bool hasStreamOptions = false;
        bool clientWantsUsage = false;

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            int depth = 0;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        continue;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        continue;
                    case JsonTokenType.PropertyName when depth == 0:
                        break;
                    default:
                        continue;
                }

                if (reader.ValueTextEquals("model"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        model = reader.GetString();
                    }
                }
                else if (reader.ValueTextEquals("stream"u8))
                {
                    if (reader.Read())
                    {
                        isStream = reader.TokenType == JsonTokenType.True;
                    }
                }
                else if (reader.ValueTextEquals("stream_options"u8))
                {
                    if (!reader.Read())
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        hasStreamOptions = true;
                        int optionsDepth = 0;
                        while (reader.Read())
                        {
                            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                            {
                                optionsDepth++;
                            }
                            else if (reader.TokenType is JsonTokenType.EndArray)
                            {
                                optionsDepth--;
                            }
                            else if (reader.TokenType == JsonTokenType.EndObject)
                            {
                                if (optionsDepth == 0)
                                {
                                    break;
                                }

                                optionsDepth--;
                            }
                            else if (reader.TokenType == JsonTokenType.PropertyName
                                && optionsDepth == 0
                                && reader.ValueTextEquals("include_usage"u8)
                                && reader.Read())
                            {
                                clientWantsUsage = reader.TokenType == JsonTokenType.True;
                            }
                        }
                    }
                    else if (reader.TokenType is JsonTokenType.StartArray)
                    {
                        // 形状不对，但不是我们要管的事：原样转发，让上游去报错。
                        hasStreamOptions = true;
                        reader.Skip();
                    }
                    else
                    {
                        hasStreamOptions = true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return default;
        }

        return new OpenAiChatRequestReading
        {
            IsValid = true,
            Model = model,
            IsStream = isStream,
            HasStreamOptions = hasStreamOptions,
            ClientWantsUsage = clientWantsUsage,
        };
    }
}
