using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Ntm.Server.Hosting;

/// <summary>
/// 没配可信代理（<see cref="TrustedProxies.Variable"/>）却收到了 <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>：
/// 多半是部署在反向代理后面但忘了配。这时登录限流和操作记录看到的都是代理的地址，
/// cookie 也不会标 Secure（Q352）。日志里提醒一次，设置页顶上也显示。
/// </summary>
internal sealed partial class ForwardedHeadersMonitor(
    IOptions<ForwardedHeadersOptions> options,
    ILogger<ForwardedHeadersMonitor> logger)
{
    private int _detected;

    /// <summary>这次启动以来见过没被采信的转发头。</summary>
    public bool Detected => Volatile.Read(ref _detected) == 1;

    public void Inspect(HttpRequest request)
    {
        if (_detected == 1 || options.Value.ForwardedHeaders != ForwardedHeaders.None)
        {
            return;
        }

        if (!request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName)
            && !request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedProtoHeaderName))
        {
            return;
        }

        if (Interlocked.Exchange(ref _detected, 1) == 0)
        {
            LogDetected(logger, request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", TrustedProxies.Variable);
        }
    }

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Warning,
        Message = "收到了来自 {RemoteAddress} 的 X-Forwarded-For / X-Forwarded-Proto，但没有配置 {Variable}，这两个头被忽略了。"
            + "如果网关在反向代理后面，请把代理的地址填进 {Variable}，否则登录限流和操作记录看到的都是代理的地址。")]
    private static partial void LogDetected(ILogger logger, string remoteAddress, string variable);
}
