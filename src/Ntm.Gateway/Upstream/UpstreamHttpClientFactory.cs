using System.Collections.Concurrent;
using System.Net;

namespace Ntm.Gateway.Upstream;

/// <summary>
/// 自己建 <see cref="SocketsHttpHandler"/>，参数照抄 YARP（ADR-0010）。
/// 用 <see cref="HttpMessageInvoker"/> 而不是 <c>HttpClient</c>：后者会缓冲响应体。
/// </summary>
public sealed class UpstreamHttpClientFactory : IDisposable
{
    /// <summary>连接超时（Q30）。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpMessageInvoker _invoker;

    // 每个不同的代理地址一个连接池（Q132）。按需建，进程内不回收：渠道数量有限，空闲连接自己会超时关掉。
    private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _proxied = new(StringComparer.Ordinal);
    private bool _disposed;

    public UpstreamHttpClientFactory()
    {
        _invoker = new HttpMessageInvoker(CreateHandler(), disposeHandler: true);
    }

    /// <summary>不走代理的出站通道。</summary>
    public HttpMessageInvoker Default => _invoker;

    /// <summary>
    /// 这个代理对应的连接池。几个渠道用同一个代理就共用一个；改了渠道的代理只是换个池，
    /// 正在进行的流不受影响（Q132）。
    /// </summary>
    public HttpMessageInvoker For(ProxyAddress? proxy)
    {
        if (proxy is null)
        {
            return _invoker;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        return _proxied.GetOrAdd(proxy.Value, _ => new Lazy<HttpMessageInvoker>(
            () => new HttpMessageInvoker(CreateHandler(proxy), disposeHandler: true))).Value;
    }

    internal static SocketsHttpHandler CreateHandler(ProxyAddress? proxy = null)
    {
        SocketsHttpHandler handler = CreateBaseHandler();
        if (proxy is not null)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy.ConnectUri) { Credentials = proxy.Credentials };
        }

        return handler;
    }

    private static SocketsHttpHandler CreateBaseHandler() => new()
    {
        // 字节透传的前提：上游压缩过的响应体必须原样转发，自动解压会改变字节（ADR-0002）。
        AutomaticDecompression = DecompressionMethods.None,

        // 网关自己管代理，不读环境变量。
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = ConnectTimeout,

        // 不设 Expect100Continue 之类的默认等待，首字节要尽快到。
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // 刻意不设整体活动超时：YARP 默认的 100 秒不会被 HTTP/2 ping 续期，
        // 长时间思考会被它切断。空闲超时由我们自己按事件间隔判断（Q30）。
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _invoker.Dispose();
        foreach (Lazy<HttpMessageInvoker> pooled in _proxied.Values)
        {
            if (pooled.IsValueCreated)
            {
                pooled.Value.Dispose();
            }
        }
    }
}
