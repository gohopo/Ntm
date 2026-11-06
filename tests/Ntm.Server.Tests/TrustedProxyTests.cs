using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Server.Hosting;

namespace Ntm.Server.Tests;

/// <summary>反向代理后面的 X-Forwarded-*（Q129）：只采信 NTM_TRUSTED_PROXIES 里列出的来源。</summary>
public sealed class TrustedProxyTests
{
    private const string RemoteIpHeader = "X-Test-Remote-Ip";

    /// <summary>TestServer 没有真实的对端地址：在整条管道最前面按测试头补一个。</summary>
    private sealed class FakeRemoteIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private static ServerHost Host(string? trusted = null) => new(
        settings: trusted is null ? null : new Dictionary<string, string> { [TrustedProxies.Variable] = trusted },
        configure: builder => builder.ConfigureServices(s => s.AddTransient<IStartupFilter, FakeRemoteIp>()));

    private static async Task<string> LoginCookieAsync(ServerHost host, string remoteIp)
    {
        using HttpClient client = host.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password = ServerHost.Password }),
        };
        request.Headers.Add(RemoteIpHeader, remoteIp);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return string.Join("\n", response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task WithoutTrustedProxiesForwardedHeadersAreIgnored()
    {
        await using ServerHost host = Host();
        string cookie = await LoginCookieAsync(host, "10.0.0.5");
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATrustedProxyCanSayTheRequestWasHttps()
    {
        await using ServerHost host = Host("10.0.0.0/8");

        Assert.Contains("secure", await LoginCookieAsync(host, "10.0.0.5"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", await LoginCookieAsync(host, "192.168.1.1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheListTakesAddressesAndCidrsAndRejectsAnythingElse()
    {
        Assert.Empty(TrustedProxies.Parse(null));
        Assert.Empty(TrustedProxies.Parse("  "));
        Assert.Equal(3, TrustedProxies.Parse("10.0.0.1, 172.16.0.0/12 ::1").Count);
        Assert.Contains(TrustedProxies.Parse("10.0.0.1"), n => n.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.Throws<FormatException>(() => TrustedProxies.Parse("10.0.0.1, proxy.local"));
    }
}
