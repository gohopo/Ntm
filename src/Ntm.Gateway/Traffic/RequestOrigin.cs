namespace Ntm.Gateway.Traffic;

/// <summary>Request 是谁发的（Q208）。</summary>
public enum RequestOrigin
{
    /// <summary>客户端拿 API 密钥打进来的。</summary>
    Client = 0,

    /// <summary>Admin 在 Workbench 里发的测试请求。不属于任何 API 密钥。</summary>
    Workbench = 1,
}
