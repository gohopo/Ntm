namespace Ntm.Protocol;

/// <summary>
/// 一种 LLM API 的请求与响应形状。入站和上游各有自己的 ApiFormat，两者不同时需要转换。
/// </summary>
public enum ApiFormat
{
    /// <summary>OpenAI Chat Completions：<c>/v1/chat/completions</c>。</summary>
    OpenAiChat = 1,

    /// <summary>OpenAI Responses：<c>/v1/responses</c>。</summary>
    OpenAiResponses = 2,

    /// <summary>Anthropic Messages：<c>/v1/messages</c>。</summary>
    Anthropic = 3,
}
