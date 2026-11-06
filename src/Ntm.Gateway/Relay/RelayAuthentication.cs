using Microsoft.AspNetCore.Http;

namespace Ntm.Gateway.Relay;

/// <summary>两种客户端的凭证读法。认证本身与入站格式无关，只有头的名字不同。</summary>
internal static class RelayAuthentication
{
    /// <summary>OpenAI 客户端用 <c>Authorization: Bearer</c>，Anthropic 客户端用 <c>x-api-key</c>；两种都认。</summary>
    public static string? ReadPresentedApiKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? authorization = request.Headers.Authorization;
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string value = authorization["Bearer ".Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        string? apiKey = request.Headers["x-api-key"];
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }
}
