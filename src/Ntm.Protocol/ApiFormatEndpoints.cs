namespace Ntm.Protocol;

/// <summary>
/// 每种 ApiFormat 的标准端点路径。Channel 的上游地址是 base URL 加可选路径，
/// 路径为空时按 ApiFormat 补这里的默认值（Q10）。
/// </summary>
public static class ApiFormatEndpoints
{
    /// <summary>该 ApiFormat 下这种请求的默认路径，含版本前缀。</summary>
    public static string? DefaultPath(ApiFormat format, RequestKind kind) => (format, kind) switch
    {
        (ApiFormat.OpenAiChat, RequestKind.Chat) => "/v1/chat/completions",
        (ApiFormat.OpenAiChat, RequestKind.Embedding) => "/v1/embeddings",
        (ApiFormat.OpenAiChat, RequestKind.Image) => "/v1/images/generations",
        (ApiFormat.OpenAiResponses, RequestKind.Chat) => "/v1/responses",
        (ApiFormat.Anthropic, RequestKind.Chat) => "/v1/messages",
        _ => null,
    };

    /// <summary>
    /// 由渠道配好的对话路径推出另一类请求的路径（Q224）：以 <c>/chat/completions</c> 结尾就把这段换成
    /// 那类请求的端点，query 原样保留。火山这类 <c>/api/v3/chat/completions</c> 的渠道因此也能接嵌入和图片。
    /// 推不出来（别的自定义路径）返回 null：这个渠道不当这类请求的候选。
    /// </summary>
    public static string? DeriveRequestPath(string chatPath, RequestKind kind)
    {
        ArgumentNullException.ThrowIfNull(chatPath);

        if (kind == RequestKind.Chat)
        {
            return chatPath;
        }

        string? suffix = kind switch
        {
            RequestKind.Embedding => "/embeddings",
            RequestKind.Image => "/images/generations",
            _ => null,
        };
        if (suffix is null)
        {
            return null;
        }

        int queryAt = chatPath.IndexOf('?', StringComparison.Ordinal);
        string path = queryAt < 0 ? chatPath : chatPath[..queryAt];
        string query = queryAt < 0 ? "" : chatPath[queryAt..];

        const string chatSuffix = "/chat/completions";
        string trimmed = path.TrimEnd('/');
        return trimmed.EndsWith(chatSuffix, StringComparison.Ordinal)
            ? string.Concat(trimmed.AsSpan(0, trimmed.Length - chatSuffix.Length), suffix, query)
            : null;
    }

    /// <summary>该 ApiFormat 列模型的默认路径。</summary>
    public static string ModelsPath(ApiFormat format) => format switch
    {
        ApiFormat.Anthropic => "/v1/models",
        _ => "/v1/models",
    };

    /// <summary>
    /// 由一个已配置的请求路径推导出列模型的路径：把已知的端点后缀换成 <c>/models</c>。
    /// 火山引擎这类把路径配成 <c>/api/v3/chat/completions</c> 的上游，模型同步要打到 <c>/api/v3/models</c>。
    /// 推不出来就返回 null，由界面要求显式填写，而不是报错（Q10）。
    /// </summary>
    public static string? DeriveModelsPath(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);

        ReadOnlySpan<string> suffixes =
        [
            "/chat/completions",
            "/responses",
            "/messages",
            "/embeddings",
            "/images/generations",
        ];

        foreach (string suffix in suffixes)
        {
            if (requestPath.EndsWith(suffix, StringComparison.Ordinal))
            {
                return string.Concat(requestPath.AsSpan(0, requestPath.Length - suffix.Length), "/models");
            }
        }

        return null;
    }
}
