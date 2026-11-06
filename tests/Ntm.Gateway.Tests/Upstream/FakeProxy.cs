using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ntm.Gateway.Tests.Upstream;

/// <summary>
/// 本机上的一个假代理，同时扮演代理后面的上游：SOCKS5 握手完了直接在隧道里回 HTTP，
/// HTTP 代理收到绝对地址的请求也直接回。记下它看到的目标主机和账号，供测试断言。
/// </summary>
internal sealed class FakeProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly bool _socks;
    private readonly (string User, string Password)? _required;

    private FakeProxy(bool socks, (string, string)? required)
    {
        _socks = socks;
        _required = required;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public static FakeProxy Socks5((string User, string Password)? required = null) => new(socks: true, required);

    public static FakeProxy Http((string User, string Password)? required = null) => new(socks: false, required);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>SOCKS5 里客户端要连的主机（域名或 IP 的文本），HTTP 代理里请求行上的绝对地址。</summary>
    public ConcurrentQueue<string> Targets { get; } = new();

    /// <summary>通过认证的账号。</summary>
    public ConcurrentQueue<string> Users { get; } = new();

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                if (_socks && !await SocksHandshakeAsync(stream))
                {
                    return;
                }

                while (await ServeHttpAsync(stream))
                {
                }
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<bool> SocksHandshakeAsync(NetworkStream stream)
    {
        CancellationToken ct = _stop.Token;
        byte[] head = new byte[2];
        await stream.ReadExactlyAsync(head, ct);
        byte[] methods = new byte[head[1]];
        await stream.ReadExactlyAsync(methods, ct);

        if (_required is { } required)
        {
            if (!methods.Contains((byte)2))
            {
                await stream.WriteAsync(new byte[] { 5, 0xFF }, ct);
                return false;
            }

            await stream.WriteAsync(new byte[] { 5, 2 }, ct);
            byte[] version = new byte[2];
            await stream.ReadExactlyAsync(version, ct);
            byte[] user = new byte[version[1]];
            await stream.ReadExactlyAsync(user, ct);
            byte[] passwordLength = new byte[1];
            await stream.ReadExactlyAsync(passwordLength, ct);
            byte[] password = new byte[passwordLength[0]];
            await stream.ReadExactlyAsync(password, ct);

            bool ok = Encoding.UTF8.GetString(user) == required.User && Encoding.UTF8.GetString(password) == required.Password;
            await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, ct);
            if (!ok)
            {
                return false;
            }

            Users.Enqueue(Encoding.UTF8.GetString(user));
        }
        else
        {
            await stream.WriteAsync(new byte[] { 5, 0 }, ct);
        }

        byte[] request = new byte[4];
        await stream.ReadExactlyAsync(request, ct);
        string host;
        switch (request[3])
        {
            case 3:
                byte[] length = new byte[1];
                await stream.ReadExactlyAsync(length, ct);
                byte[] name = new byte[length[0]];
                await stream.ReadExactlyAsync(name, ct);
                host = Encoding.ASCII.GetString(name);
                break;
            case 1:
                byte[] v4 = new byte[4];
                await stream.ReadExactlyAsync(v4, ct);
                host = new IPAddress(v4).ToString();
                break;
            default:
                byte[] v6 = new byte[16];
                await stream.ReadExactlyAsync(v6, ct);
                host = new IPAddress(v6).ToString();
                break;
        }

        byte[] port = new byte[2];
        await stream.ReadExactlyAsync(port, ct);
        Targets.Enqueue(host);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
        return true;
    }

    /// <summary>读一个 HTTP 请求并回一个响应。连接被关了就返回 false。</summary>
    private async Task<bool> ServeHttpAsync(NetworkStream stream)
    {
        var header = new StringBuilder();
        byte[] one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                return false;
            }

            header.Append((char)one[0]);
        }

        string[] lines = header.ToString().Split("\r\n");
        int contentLength = 0;
        string? proxyAuthorization = null;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                proxyAuthorization = value;
            }
        }

        if (contentLength > 0)
        {
            await stream.ReadExactlyAsync(new byte[contentLength], _stop.Token);
        }

        if (!_socks)
        {
            Targets.Enqueue(lines[0].Split(' ')[1]);
            if (_required is { } required)
            {
                string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{required.User}:{required.Password}"));
                if (proxyAuthorization != expected)
                {
                    await WriteAsync(stream, "407 Proxy Authentication Required", "", "Proxy-Authenticate: Basic realm=\"fake\"\r\n");
                    return true;
                }

                Users.Enqueue(required.User);
            }
        }

        await WriteAsync(stream, "200 OK", """{"choices":[{"message":{"role":"assistant","content":"via proxy"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""");
        return true;
    }

    private async Task WriteAsync(NetworkStream stream, string status, string body, string extraHeaders = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n{extraHeaders}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
        await stream.WriteAsync(bytes, _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
