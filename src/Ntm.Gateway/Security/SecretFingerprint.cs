using System.Security.Cryptography;
using System.Text;

namespace Ntm.Gateway.Security;

/// <summary>
/// 凭证的不可逆指纹。ApiKey 按它查（ADR-0006），ChannelKey 的冷却与限速计数也按它归集（Q21）。
///
/// 用裸 SHA-256 而不是密码派生函数：这两种凭证都是高熵随机串，不存在字典猜测，
/// 而登录口令走的是另一条路（bcrypt 之类），不要混用。
/// </summary>
public static class SecretFingerprint
{
    /// <summary>前缀保留的字符数。</summary>
    public const int PrefixLength = 12;

    /// <summary>算指纹。</summary>
    public static string Compute(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 取给界面和日志看的前缀：最多 <see cref="PrefixLength"/> 个字符，且不超过原文长度的三分之一。
    /// 后一条是给短密钥的——自建上游常见 8 位口令，按固定长度截会把整个原文露出来。
    /// </summary>
    public static string Prefix(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        return secret[..Math.Min(PrefixLength, secret.Length / 3)];
    }

    /// <summary>生成一个新的 API 密钥原文。</summary>
    public static string GenerateApiKey() => "ntm-" + RandomNumberGenerator.GetHexString(48, lowercase: true);

    /// <summary>定时比较，避免按指纹查询时泄露匹配进度。</summary>
    public static bool Matches(string fingerprint, string other) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(fingerprint),
            Encoding.UTF8.GetBytes(other));
}
