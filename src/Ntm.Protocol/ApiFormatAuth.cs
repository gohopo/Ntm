namespace Ntm.Protocol;

/// <summary>每种 ApiFormat 怎么带凭证。只描述，不构造请求——Protocol 不认识 HTTP 类型（ADR-0007）。</summary>
public readonly record struct ApiFormatAuthScheme
{
    private readonly IReadOnlyList<KeyValuePair<string, string>>? _additionalHeaders;

    /// <summary>放凭证的请求头名。</summary>
    public required string HeaderName { get; init; }

    /// <summary>头值前缀，例如 <c>Bearer </c>。为空表示直接放原值。</summary>
    public string? ValuePrefix { get; init; }

    /// <summary>这个格式额外要求的固定头，例如 Anthropic 的版本头。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AdditionalHeaders
    {
        get => _additionalHeaders ?? [];
        init => _additionalHeaders = value;
    }
}

/// <summary>各 ApiFormat 的凭证位置。</summary>
public static class ApiFormatAuth
{
    /// <summary>
    /// Anthropic 要求显式带版本头，缺了会被拒。入站请求如果自带了这个头，透传时以客户端的为准。
    /// </summary>
    public const string AnthropicVersionHeader = "anthropic-version";

    public const string AnthropicDefaultVersion = "2023-06-01";

    /// <summary>
    /// <paramref name="anthropicBearer"/>：Anthropic 渠道把密钥放进 <c>Authorization: Bearer</c>
    /// 而不是 <c>x-api-key</c>（Q166）。不少中转只认前者。两者永不同时带。
    /// </summary>
    public static ApiFormatAuthScheme For(ApiFormat format, bool anthropicBearer = false) => format switch
    {
        ApiFormat.Anthropic => new ApiFormatAuthScheme
        {
            HeaderName = anthropicBearer ? "Authorization" : "x-api-key",
            ValuePrefix = anthropicBearer ? "Bearer " : null,
            AdditionalHeaders = [new(AnthropicVersionHeader, AnthropicDefaultVersion)],
        },
        _ => new ApiFormatAuthScheme
        {
            HeaderName = "Authorization",
            ValuePrefix = "Bearer ",
        },
    };
}
