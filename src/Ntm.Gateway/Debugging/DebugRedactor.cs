using System.Collections.Frozen;
using System.Text;

namespace Ntm.Gateway.Debugging;

/// <summary>
/// 请求头脱敏（Q158）：固定的几个认证头只留前缀，另外凡是值里出现了已知密钥的，也把那一段换掉。
/// 正文不脱敏——改写后的请求体和上游响应本来就是要拿来对照的。
/// </summary>
public static class DebugRedactor
{
    /// <summary>这些头的值一定是凭证，整体换掉。</summary>
    public static readonly FrozenSet<string> SensitiveHeaders = new[]
    {
        "authorization",
        "x-api-key",
        "api-key",
        "proxy-authorization",
        "cookie",
        "set-cookie",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>短于这个长度的「密钥」不去正文里找：太容易误伤普通文本。</summary>
    private const int MinSecretLength = 8;

    private const string Mask = "***";

    /// <summary>留几个字符方便认出是哪一把。</summary>
    private const int KeptPrefixLength = 6;

    /// <summary>把一组头拼成一行一个 <c>name: value</c>，顺手脱敏。</summary>
    public static string Format(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        IReadOnlyCollection<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var text = new StringBuilder();
        foreach ((string name, IEnumerable<string> values) in headers)
        {
            foreach (string value in values)
            {
                text.Append(name).Append(": ").Append(Redact(name, value, secrets)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>一个头的值脱敏后的样子。</summary>
    public static string Redact(string name, string value, IReadOnlyCollection<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (SensitiveHeaders.Contains(name))
        {
            return RedactCredential(value);
        }

        if (secrets is null)
        {
            return value;
        }

        // 自定义头里也可能塞着密钥（有些中转要求 api-key 走别的头名）。
        string result = value;
        foreach (string secret in secrets)
        {
            if (secret.Length >= MinSecretLength && result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, Keep(secret), StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary>认证头：保留 <c>Bearer</c> 这类方案名和凭证的头几个字符。</summary>
    private static string RedactCredential(string value)
    {
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && space < value.Length - 1)
        {
            return value[..(space + 1)] + Keep(value[(space + 1)..]);
        }

        return Keep(value);
    }

    private static string Keep(string secret) =>
        secret.Length <= KeptPrefixLength ? Mask : secret[..KeptPrefixLength] + Mask;
}
