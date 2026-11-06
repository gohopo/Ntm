using System.Diagnostics.CodeAnalysis;
using Ntm.Protocol;

namespace Ntm.Gateway.Upstream;

/// <summary>
/// Channel 的上游地址：base URL 加可选路径。base URL 不含版本前缀（Q10）；
/// 路径为空时按 ApiFormat 补默认值。
/// </summary>
public static class UpstreamAddress
{
    /// <summary>
    /// 规范化用户填的 base URL。末尾的版本段（<c>/v1</c>、<c>/v3</c>、<c>/v1beta</c>）会被剥掉并作为路径前缀返回，
    /// 这样把 SDK 风格的 <c>https://api.openai.com/v1</c> 整个粘进来也不会拼成 <c>/v1/v1/chat/completions</c>。
    /// </summary>
    /// <param name="input">用户填的 base URL。</param>
    /// <param name="baseUrl">规范化后的 base URL，不带尾斜杠。</param>
    /// <param name="versionPrefix">从 base URL 末尾剥下来的版本段，没有就是空。</param>
    public static bool TryNormalizeBaseUrl(
        string? input,
        [NotNullWhen(true)] out string? baseUrl,
        out string versionPrefix)
    {
        baseUrl = null;
        versionPrefix = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        var segments = new List<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries));

        // 从末尾连续剥版本段：/api/v3 剥成 /api + /v3，/v1 剥成空 + /v1。
        var stripped = new List<string>();
        while (segments.Count > 0 && IsVersionSegment(segments[^1]))
        {
            stripped.Insert(0, segments[^1]);
            segments.RemoveAt(segments.Count - 1);
        }

        string remaining = segments.Count == 0 ? string.Empty : "/" + string.Join('/', segments);
        baseUrl = $"{uri.Scheme}://{uri.Authority}{remaining}";
        versionPrefix = stripped.Count == 0 ? string.Empty : "/" + string.Join('/', stripped);
        return true;
    }

    /// <summary>
    /// 决定 Channel 要存的路径。用户填了就用他的；没填时，如果 base URL 带了非 <c>/v1</c> 的版本段
    /// （火山的 <c>/api/v3</c>），用它换掉默认路径里的 <c>/v1</c>，否则会请求到 <c>/api/v1/chat/completions</c>。
    /// 结果等于默认路径时返回 null，表示「按 API 格式默认」。
    /// </summary>
    /// <param name="path">用户填的路径。</param>
    /// <param name="versionPrefix"><see cref="TryNormalizeBaseUrl"/> 剥下来的版本段。</param>
    /// <param name="format">渠道的 API 格式。</param>
    public static string? ResolveChannelPath(string? path, string versionPrefix, ApiFormat format)
    {
        ArgumentNullException.ThrowIfNull(versionPrefix);

        if (!string.IsNullOrWhiteSpace(path))
        {
            return path.Trim();
        }

        string? defaultPath = ApiFormatEndpoints.DefaultPath(format, RequestKind.Chat);
        if (versionPrefix.Length == 0 || defaultPath is null || !defaultPath.StartsWith("/v1/", StringComparison.Ordinal))
        {
            return null;
        }

        string resolved = versionPrefix + defaultPath["/v1".Length..];
        return resolved == defaultPath ? null : resolved;
    }

    /// <summary>
    /// 拼出最终请求地址。<paramref name="path"/> 为空时按 ApiFormat 补默认路径。
    /// 渠道存的路径是对话路径：别的请求从它推导（<see cref="ApiFormatEndpoints.DeriveRequestPath"/>），推不出来返回 false。
    /// </summary>
    public static bool TryBuildRequestUri(
        string baseUrl,
        string? path,
        ApiFormat format,
        RequestKind kind,
        [NotNullWhen(true)] out Uri? requestUri)
    {
        requestUri = null;

        string? effective = string.IsNullOrWhiteSpace(path)
            ? ApiFormatEndpoints.DefaultPath(format, kind)
            : ApiFormatCapability.Supports(format, kind)
                ? ApiFormatEndpoints.DeriveRequestPath(path.Trim(), kind)
                : null;

        if (effective is null)
        {
            // 这个 ApiFormat 不支持这种请求，调用方应该先用 ApiFormatCapability 过滤掉。
            return false;
        }

        if (!effective.StartsWith('/'))
        {
            effective = "/" + effective;
        }

        return Uri.TryCreate(baseUrl.TrimEnd('/') + effective, UriKind.Absolute, out requestUri);
    }

    /// <summary>
    /// 形如 <c>v1</c>、<c>v3</c>、<c>v1beta</c>、<c>v2alpha</c> 的版本段。
    /// 不含纯数字（<c>/3</c> 不是版本段）。
    /// </summary>
    private static bool IsVersionSegment(string segment)
    {
        if (segment.Length < 2 || (segment[0] != 'v' && segment[0] != 'V'))
        {
            return false;
        }

        int i = 1;
        while (i < segment.Length && char.IsAsciiDigit(segment[i]))
        {
            i++;
        }

        if (i == 1)
        {
            return false;
        }

        // 版本号后面可以跟 beta / alpha 这类后缀。
        return i == segment.Length
            || segment.AsSpan(i).Equals("beta", StringComparison.OrdinalIgnoreCase)
            || segment.AsSpan(i).Equals("alpha", StringComparison.OrdinalIgnoreCase);
    }
}
