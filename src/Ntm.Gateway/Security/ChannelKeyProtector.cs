using Microsoft.AspNetCore.DataProtection;

namespace Ntm.Gateway.Security;

/// <summary>
/// ChannelKey 的密钥值必须能还原——它要原样发给上游——所以用 Data Protection 加密而不是哈希（ADR-0006）。
/// 密钥环落在数据目录里，必须和库一起备份，丢了就要重新录入所有渠道密钥。
/// </summary>
public sealed class ChannelKeyProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "Ntm.ChannelKey.v1";

    /// <summary>渠道代理地址里常带账号密码，整段同样加密（Q131）。用另一个 purpose，两种密文不能互换。</summary>
    private const string ProxyPurpose = "Ntm.ChannelProxy.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);
    private readonly IDataProtector _proxyProtector = provider.CreateProtector(ProxyPurpose);

    public string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return _protector.Protect(secret);
    }

    public string Unprotect(string protectedSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedSecret);
        return _protector.Unprotect(protectedSecret);
    }

    public string ProtectProxy(string proxyUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(proxyUrl);
        return _proxyProtector.Protect(proxyUrl);
    }

    public string UnprotectProxy(string protectedProxyUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedProxyUrl);
        return _proxyProtector.Unprotect(protectedProxyUrl);
    }
}
