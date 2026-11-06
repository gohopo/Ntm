using System.Text.Json;
using Ntm.Protocol;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 上游没给用量时按字符估算（Q47）。典型场景是客户端中途断开：上游的 usage 块永远不会到。
///
/// 启发式：ASCII 约 4 字符 / Token，其他文字（中日韩、西里尔、阿拉伯…）约 1 字符 / Token，
/// 每张图固定 <see cref="TokensPerImage"/>。输入全部按非缓存算，是个上界——
/// 估算只用于封顶和展示，宁可高估也不要让断开变成免费（Q46）。
///
/// 这里刻意只用 BCL，不引分词器。要换成 Microsoft.ML.Tokenizers 的 o200k
/// 或按渠道 / 模型校准时，只改这一个文件。
/// </summary>
public static class UsageEstimator
{
    /// <summary>一张图按多少 Token 算。各家公式差别很大，取一个中间值。</summary>
    public const long TokensPerImage = 1000;

    private const int AsciiCharsPerToken = 4;

    /// <summary>base64 字段按这个长度起才当附件，再短就当普通文本。</summary>
    private const int Base64Threshold = 256;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 按请求体和已看到的输出字符数估一份用量。
    /// </summary>
    /// <param name="requestBody">发给上游的请求体原文（JSON）。</param>
    /// <param name="outputAsciiChars">已经收到的输出里的 ASCII 字符数。</param>
    /// <param name="outputWideChars">已经收到的输出里的非 ASCII 字符数。</param>
    /// <returns>来源标为 <see cref="UsageSource.GatewayEstimated"/> 的用量。</returns>
    public static Usage Estimate(ReadOnlySpan<byte> requestBody, long outputAsciiChars, long outputWideChars) =>
        new()
        {
            InputTokens = EstimateInputTokens(requestBody),
            OutputTokens = TokensFromChars(outputAsciiChars, outputWideChars),
            Source = UsageSource.GatewayEstimated,
        };

    /// <summary>
    /// 嵌入请求的上游没给用量时（Q229）：只数 <c>input</c>。字符串按字符估，
    /// 整数数组本身就是 Token id，一个元素算一个 Token。嵌入没有输出。
    /// </summary>
    /// <param name="requestBody">发给上游的请求体原文（JSON）。</param>
    public static Usage EstimateEmbedding(ReadOnlySpan<byte> requestBody)
    {
        long ascii = 0;
        long wide = 0;
        long ids = 0;

        try
        {
            using JsonDocument document = JsonDocument.Parse(requestBody.ToArray(), DocumentOptions);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("input", out JsonElement input))
            {
                WalkEmbeddingInput(input, ref ascii, ref wide, ref ids);
            }
        }
        catch (JsonException)
        {
            return Estimate(requestBody, 0, 0);
        }

        return new Usage
        {
            InputTokens = TokensFromChars(ascii, wide) + ids,
            Source = UsageSource.GatewayEstimated,
        };
    }

    private static void WalkEmbeddingInput(JsonElement element, ref long ascii, ref long wide, ref long ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WalkEmbeddingInput(item, ref ascii, ref wide, ref ids);
                }

                break;

            case JsonValueKind.Number:
                ids++;
                break;

            case JsonValueKind.String:
                foreach (char c in element.GetString() ?? "")
                {
                    if (char.IsAscii(c))
                    {
                        ascii++;
                    }
                    else
                    {
                        wide++;
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 数请求体里所有字符串字段的字符数，长 base64 字段按一张图算。
    /// 解析不了就退回按字节数估（UTF-8 的 ASCII 一字节一字符）。
    /// </summary>
    private static long EstimateInputTokens(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return 0;
        }

        long ascii = 0;
        long wide = 0;
        long images = 0;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray(), DocumentOptions);
            Walk(document.RootElement, ref ascii, ref wide, ref images);
        }
        catch (JsonException)
        {
            // 请求体不是 JSON（不该发生，转发前校验过）：退回按字节估。
            return TokensFromChars(body.Length, cjkChars: 0);
        }

        return TokensFromChars(ascii, wide) + (images * TokensPerImage);
    }

    private static void Walk(JsonElement element, ref long ascii, ref long wide, ref long images)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, ref ascii, ref wide, ref images);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, ref ascii, ref wide, ref images);
                }

                break;

            case JsonValueKind.String:
                Count(element.GetString(), ref ascii, ref wide, ref images);
                break;

            default:
                break;
        }
    }

    private static void Count(string? text, ref long ascii, ref long wide, ref long images)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 图片与文件是 base64 或 data: URL，按字符数算会把一张图当成几十万 Token。
        if (text.Length >= Base64Threshold && LooksLikeBase64(text))
        {
            images++;
            return;
        }

        foreach (char c in text)
        {
            if (char.IsAscii(c))
            {
                ascii++;
            }
            else
            {
                wide++;
            }
        }
    }

    /// <summary>ASCII 按 4 字符 / Token，其他文字按 1 字符 / Token，都向上取整。</summary>
    private static long TokensFromChars(long asciiChars, long cjkChars) =>
        ((asciiChars + AsciiCharsPerToken - 1) / AsciiCharsPerToken) + cjkChars;

    /// <summary>
    /// 粗判是不是 base64 附件：data: URL，或者只由 base64 字母表组成的长串。
    /// 只看前 512 个字符，够排除正常文本了。
    /// </summary>
    private static bool LooksLikeBase64(string text)
    {
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int limit = Math.Min(text.Length, 512);
        for (int i = 0; i < limit; i++)
        {
            char c = text[i];
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
