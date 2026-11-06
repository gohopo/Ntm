using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Ntm.Gateway.Upstream;

/// <summary>
/// 一个渠道的出站代理（Q130）。支持 http、https、socks4、socks4a、socks5、socks5h。
/// socks5h 原样保存，连接时按 socks5 走：.NET 的 socks5 本来就把域名交给代理去解析，和 socks5h 是同一个意思。
/// </summary>
public sealed record ProxyAddress
{
    public const int MaxLength = 500;

    private static readonly Dictionary<string, int> DefaultPorts = new(StringComparer.Ordinal)
    {
        ["http"] = 80,
        ["https"] = 443,
        ["socks4"] = 1080,
        ["socks4a"] = 1080,
        ["socks5"] = 1080,
        ["socks5h"] = 1080,
    };

    private ProxyAddress(string value, string scheme, string host, int port, string? userName, string? password)
    {
        Value = value;
        Scheme = scheme;
        Host = host;
        Port = port;
        UserName = userName;
        Password = password;
    }

    /// <summary>录入时的原文。它也是连接池的键：用户名或密码不同就是不同的池。</summary>
    public string Value { get; }

    /// <summary>小写的 scheme，socks5h 保持原样。</summary>
    public string Scheme { get; }

    /// <summary>主机名；IPv6 地址带方括号。</summary>
    public string Host { get; }

    public int Port { get; }

    public string? UserName { get; }

    public string? Password { get; }

    /// <summary>给 <see cref="WebProxy"/> 的地址：不带用户信息，socks5h 换成 socks5。</summary>
    public Uri ConnectUri => new($"{(Scheme == "socks5h" ? "socks5" : Scheme)}://{Host}:{Port}");

    public NetworkCredential? Credentials => UserName is null ? null : new NetworkCredential(UserName, Password ?? "");

    /// <summary>错误信息里用的「host:port」。不含用户名和密码。</summary>
    public string Endpoint => $"{Host}:{Port}";

    /// <summary>界面上显示的样子：密码换成 ***（Q131）。</summary>
    public string Masked => UserName is null
        ? $"{Scheme}://{Endpoint}"
        : $"{Scheme}://{Uri.EscapeDataString(UserName)}{(Password is null ? "" : ":***")}@{Endpoint}";

    public override string ToString() => Masked;

    public static bool TryParse(string? value, [NotNullWhen(true)] out ProxyAddress? proxy, [NotNullWhen(false)] out string? error)
    {
        proxy = null;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "请填写代理地址。";
            return false;
        }

        if (value.Length > MaxLength)
        {
            error = $"代理地址不能超过 {MaxLength} 个字符。";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            error = "代理地址要写成 scheme://host:port，例如 socks5://127.0.0.1:1080。";
            return false;
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        if (!DefaultPorts.TryGetValue(scheme, out int defaultPort))
        {
            error = "代理只支持 http、https、socks4、socks4a、socks5 和 socks5h。";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "代理地址缺少主机名。";
            return false;
        }

        if (uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "代理地址只要 scheme://host:port，不能带路径或参数。";
            return false;
        }

        string? userName = null;
        string? password = null;
        if (uri.UserInfo.Length > 0)
        {
            int colon = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            userName = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            password = colon < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        }

        int port = uri.IsDefaultPort || uri.Port < 0 ? defaultPort : uri.Port;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;

        proxy = new ProxyAddress(value, scheme, host, port, userName, password);
        error = null;
        return true;
    }
}
