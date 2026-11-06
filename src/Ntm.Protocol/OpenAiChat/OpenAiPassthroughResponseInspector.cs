using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>读一次非流式的嵌入或图片响应得到的结果。只读，字节原样交给客户端（Q228）。</summary>
public readonly record struct OpenAiPassthroughResponseReading
{
    /// <summary>响应体是错误（顶层 <c>error</c> 非空）。HTTP 可能仍然是 200。</summary>
    public bool IsError { get; init; }

    /// <summary>不是一个能解析的 JSON 对象。</summary>
    public bool IsMalformed { get; init; }

    /// <summary>
    /// 2xx、是 JSON 对象、没有错误，但一条结果都没有：嵌入的 <c>data</c> 是空的，
    /// 或者图片的 <c>data</c> 和 <c>images</c> 都没有条目。按软错误处理，换候选（Q228）。
    /// </summary>
    public bool IsEmpty { get; init; }

    public string? ErrorPayload { get; init; }

    public Usage Usage { get; init; }

    public string? UpstreamModel { get; init; }

    /// <summary>结果条数：嵌入是向量个数，图片是张数（Q230）。</summary>
    public int ItemCount { get; init; }
}

/// <summary>
/// 嵌入和图片只在 OpenAI 格式之间透传（Q221），这里只认出成败、用量和条数，不碰向量和图片本身：
/// 向量可能是 base64（new-api #1400 就是解码成浮点数组弄坏的），图片可能是几十 MB 的 b64_json。
/// </summary>
public static class OpenAiPassthroughResponseInspector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static OpenAiPassthroughResponseReading Read(ReadOnlySpan<byte> body, RequestKind kind)
    {
        if (body.IsEmpty)
        {
            return new OpenAiPassthroughResponseReading { IsMalformed = true };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), DocumentOptions);
        }
        catch (JsonException)
        {
            return new OpenAiPassthroughResponseReading { IsMalformed = true };
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OpenAiPassthroughResponseReading { IsMalformed = true };
            }

            if (OpenAiPassthroughJson.TryReadError(root, out string? error))
            {
                return new OpenAiPassthroughResponseReading { IsError = true, ErrorPayload = error };
            }

            int items = OpenAiPassthroughJson.CountItems(root, kind);
            return new OpenAiPassthroughResponseReading
            {
                IsEmpty = items == 0,
                ItemCount = items,
                Usage = OpenAiPassthroughJson.TryReadUsage(root, kind, out Usage usage) ? usage : Usage.Unavailable,
                UpstreamModel = root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String
                    ? model.GetString()
                    : null,
            };
        }
    }
}

/// <summary>嵌入和图片响应共用的几段 JSON 读法。</summary>
internal static class OpenAiPassthroughJson
{
    public static bool TryReadError(JsonElement root, out string? payload)
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

    /// <summary>
    /// 结果条数。图片认 OpenAI 的 <c>data</c>，也认硅基流动的 <c>images</c>（两者取大的）。
    /// </summary>
    public static int CountItems(JsonElement root, RequestKind kind)
    {
        int data = ArrayLength(root, "data");
        if (kind != RequestKind.Image)
        {
            return data;
        }

        // 火山只对成功的图计费，并在 usage.generated_images 里给出张数：有就以它为准。
        if (root.TryGetProperty("usage", out JsonElement usage)
            && usage.ValueKind == JsonValueKind.Object
            && ReadLong(usage, "generated_images") is { } generated and >= 0 and <= int.MaxValue)
        {
            return (int)generated;
        }

        return Math.Max(data, ArrayLength(root, "images"));
    }

    /// <summary>
    /// 把用量换成五项桶（ADR-0005）。嵌入只有输入：<c>prompt_tokens</c>，没有就用 <c>total_tokens</c>。
    /// 图片是 gpt-image 的 <c>input_tokens</c>/<c>output_tokens</c>，也接受 <c>prompt_tokens</c>/<c>completion_tokens</c>（Q229）。
    /// 图片接口没有提示缓存，缓存几项恒为 0。
    /// </summary>
    public static bool TryReadUsage(JsonElement root, RequestKind kind, out Usage usage)
    {
        usage = Usage.Unavailable;
        if (!root.TryGetProperty("usage", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        long input;
        long output;
        if (kind == RequestKind.Embedding)
        {
            input = ReadLong(node, "prompt_tokens") ?? ReadLong(node, "total_tokens") ?? -1;
            output = 0;
        }
        else
        {
            long? inputTokens = ReadLong(node, "input_tokens") ?? ReadLong(node, "prompt_tokens");
            long? outputTokens = ReadLong(node, "output_tokens") ?? ReadLong(node, "completion_tokens");
            if (inputTokens is null && outputTokens is null)
            {
                // 只给了 total_tokens 之类：拿不准是输入还是输出，不算 Token。
                return false;
            }

            input = inputTokens ?? 0;
            output = outputTokens ?? 0;
        }

        if (input < 0)
        {
            return false;
        }

        usage = new Usage
        {
            InputTokens = input,
            CacheReadTokens = 0,
            CacheWrite5mTokens = 0,
            CacheWrite1hTokens = 0,
            OutputTokens = Math.Max(0, output),
            Source = UsageSource.Upstream,
        };
        return true;
    }

    public static long? ReadLong(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : null;

    private static int ArrayLength(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
}
