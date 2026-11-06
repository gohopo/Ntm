using System.Text.Json;

namespace Ntm.Protocol.OpenAiChat;

/// <summary>
/// 逐帧读图片生成的流（Q232）。只读，字节原样转给客户端。认得两种事件名：
/// OpenAI / OpenRouter 的 <c>image_generation.partial_image</c>、<c>image_generation.completed</c>，
/// 火山的 <c>image_generation.partial_succeeded</c>、<c>partial_failed</c>、<c>completed</c>。
/// 结束靠 <c>*.completed</c> 判断，不等 <c>[DONE]</c>：OpenAI 不发它。
/// </summary>
public sealed class OpenAiImageStreamInspector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private int _completedEvents;
    private int _succeededEvents;
    private int? _generatedImages;

    /// <summary>见过带图的事件没有（部分图、成图）。第一个这样的帧写给客户端即为 Commit。</summary>
    public bool HasContentEvent { get; private set; }

    /// <summary>见过 <c>*.completed</c> 或 <c>[DONE]</c>。</summary>
    public bool SawTerminal { get; private set; }

    /// <summary>用量只在 completed 事件里有。</summary>
    public Usage Usage { get; private set; }

    public string? ErrorPayload { get; private set; }

    /// <summary>火山单张失败（<c>partial_failed</c>）的原文。一张都没成时拿它当失败原因。</summary>
    public string? PartialFailure { get; private set; }

    public string? UpstreamModel { get; private set; }

    /// <summary>
    /// 生成的张数（Q230）：上游在 usage 里给了 <c>generated_images</c> 就用它；
    /// 火山按 <c>partial_succeeded</c> 的个数；OpenAI 按 <c>completed</c> 的个数。
    /// </summary>
    public int ImageCount => _generatedImages ?? (_succeededEvents > 0 ? _succeededEvents : _completedEvents);

    /// <summary>流结束了但一张图都没有。</summary>
    public bool EndedEmpty => SawTerminal && ImageCount == 0 && !HasContentEvent;

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

            string? type = root.TryGetProperty("type", out JsonElement typeNode) && typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()
                : null;

            // 火山的单张失败也带 error 字段，但流还在继续，别的图可能成功：记下来，不当成流错误。
            if (type is not null && type.EndsWith(".partial_failed", StringComparison.Ordinal))
            {
                PartialFailure ??= root.GetRawText();
                return StreamFrameVerdict.Preamble;
            }

            if (OpenAiPassthroughJson.TryReadError(root, out string? error))
            {
                ErrorPayload = error;
                return StreamFrameVerdict.Error;
            }

            if (root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
            {
                UpstreamModel = model.GetString();
            }

            if (OpenAiPassthroughJson.TryReadUsage(root, RequestKind.Image, out Usage usage))
            {
                Usage = usage;
            }

            if (root.TryGetProperty("usage", out JsonElement usageNode)
                && usageNode.ValueKind == JsonValueKind.Object
                && OpenAiPassthroughJson.ReadLong(usageNode, "generated_images") is { } generated and >= 0 and <= int.MaxValue)
            {
                _generatedImages = (int)generated;
            }

            switch (type)
            {
                case not null when type.EndsWith(".partial_image", StringComparison.Ordinal):
                    HasContentEvent = true;
                    return StreamFrameVerdict.Content;

                case not null when type.EndsWith(".partial_succeeded", StringComparison.Ordinal):
                    _succeededEvents++;
                    HasContentEvent = true;
                    return StreamFrameVerdict.Content;

                case not null when type.EndsWith(".completed", StringComparison.Ordinal):
                    SawTerminal = true;
                    bool carriesImage = HasNonEmptyString(root, "b64_json") || HasNonEmptyString(root, "url");
                    if (carriesImage)
                    {
                        _completedEvents++;
                    }

                    // 火山的 completed 只带用量；它前面的 partial_succeeded 已经提交过了。
                    if (carriesImage || HasContentEvent)
                    {
                        HasContentEvent = true;
                        return StreamFrameVerdict.Content;
                    }

                    return StreamFrameVerdict.UsageOnly;

                default:
                    return StreamFrameVerdict.Preamble;
            }
        }
    }

    private static bool HasNonEmptyString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 };
}
