namespace Ntm.Protocol;

/// <summary>
/// 一次请求要做的事。只有 <see cref="Chat"/> 经 IR 转换，其余只在相同 ApiFormat 之间转发（ADR-0001）。
/// </summary>
public enum RequestKind
{
    Chat = 1,
    Embedding = 2,
    Image = 3,
    Audio = 4,
    Rerank = 5,
}

/// <summary>
/// 每种 <see cref="ApiFormat"/> 支持哪些 <see cref="RequestKind"/>。
/// 选路时用它过滤候选，而不是等转换器抛异常——参考实现（AxonHub）只在列出模型时查能力，
/// 于是嵌入请求被路由到 Anthropic 渠道后死在转换器里。
/// </summary>
public static class ApiFormatCapability
{
    /// <summary>该 ApiFormat 能不能服务这种请求。</summary>
    public static bool Supports(ApiFormat format, RequestKind kind) => format switch
    {
        // OpenAI 兼容端点普遍提供嵌入与图片；音频与重排留到真有渠道需要时再开。
        ApiFormat.OpenAiChat => kind is RequestKind.Chat or RequestKind.Embedding or RequestKind.Image,
        ApiFormat.OpenAiResponses => kind is RequestKind.Chat,
        // Anthropic 只有 Messages，没有嵌入也没有图片端点。
        ApiFormat.Anthropic => kind is RequestKind.Chat,
        _ => false,
    };
}
