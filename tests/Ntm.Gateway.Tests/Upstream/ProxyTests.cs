using System.Net;
using System.Net.Sockets;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Tests.Forwarding;
using Ntm.Gateway.Traffic;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Upstream;

/// <summary>渠道出站代理（Q130–Q133）：地址解析、每个代理一个连接池、经代理的失败归到整个渠道。</summary>
public sealed class ProxyTests
{
    private static ProxyAddress Parse(string value)
    {
        Assert.True(ProxyAddress.TryParse(value, out ProxyAddress? proxy, out string? error), error);
        return proxy;
    }

    private static ForwardRequest Request(ProxyAddress? proxy) => new()
    {
        // 上游用域名：SOCKS5 应该把域名交给代理去解析，而不是自己先解析。
        RequestUri = new Uri("http://upstream.test/v1/chat/completions"),
        Proxy = proxy,
        Secret = "sk-test",
        UpstreamApiFormat = ApiFormat.OpenAiChat,
        InboundApiFormat = ApiFormat.OpenAiChat,
        Body = "{\"model\":\"gpt-4o\"}"u8.ToArray(),
        IsStream = false,
    };

    private static async Task<ForwardOutcome> ForwardAsync(UpstreamHttpClientFactory factory, ProxyAddress? proxy)
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await new UpstreamForwarder(factory).ForwardAsync(Request(proxy), committer, timeout.Token);
    }

    [Theory]
    [InlineData("http://proxy.local", "http", 80)]
    [InlineData("https://proxy.local", "https", 443)]
    [InlineData("socks4://10.0.0.1", "socks4", 1080)]
    [InlineData("socks4a://10.0.0.1:1081", "socks4a", 1081)]
    [InlineData("SOCKS5://10.0.0.1:7890", "socks5", 7890)]
    [InlineData("socks5h://[::1]:1080", "socks5h", 1080)]
    public void SupportedSchemesParseWithTheirDefaultPorts(string value, string scheme, int port)
    {
        ProxyAddress proxy = Parse(value);
        Assert.Equal(scheme, proxy.Scheme);
        Assert.Equal(port, proxy.Port);
    }

    [Fact]
    public void Socks5hConnectsAsSocks5AndCredentialsAreMasked()
    {
        ProxyAddress proxy = Parse("socks5h://us%40er:p%3Ass@proxy.local:1080");

        Assert.Equal("socks5", proxy.ConnectUri.Scheme);
        Assert.Equal("", proxy.ConnectUri.UserInfo);
        Assert.Equal("us@er", proxy.UserName);
        Assert.Equal("p:ss", proxy.Password);
        Assert.Equal("socks5h://us%40er:***@proxy.local:1080", proxy.Masked);
        Assert.Equal("proxy.local:1080", proxy.Endpoint);
        Assert.DoesNotContain("p:ss", proxy.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxy.local:1080")]
    [InlineData("ftp://proxy.local")]
    [InlineData("socks5://proxy.local/path")]
    [InlineData("http://proxy.local/?x=1")]
    public void AnythingElseIsRejected(string value)
    {
        Assert.False(ProxyAddress.TryParse(value, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void EachDistinctProxyGetsItsOwnPoolAndNoProxyUsesTheDefault()
    {
        using var factory = new UpstreamHttpClientFactory();
        ProxyAddress a = Parse("socks5://127.0.0.1:1080");

        Assert.Same(factory.Default, factory.For(null));
        Assert.Same(factory.For(a), factory.For(Parse("socks5://127.0.0.1:1080")));
        Assert.NotSame(factory.For(a), factory.For(Parse("socks5://u:p@127.0.0.1:1080")));
    }

    [Theory]
    [InlineData("socks5")]
    [InlineData("socks5h")]
    public async Task Socks5PassesTheHostNameAndCredentialsToTheProxy(string scheme)
    {
        await using FakeProxy fake = FakeProxy.Socks5(("alice", "s3cret"));
        using var factory = new UpstreamHttpClientFactory();

        ForwardOutcome outcome = await ForwardAsync(factory, Parse($"{scheme}://alice:s3cret@127.0.0.1:{fake.Port}"));

        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.Equal(["upstream.test"], fake.Targets);
        Assert.Equal(["alice"], fake.Users);
    }

    [Fact]
    public async Task AnHttpProxyIsAnsweredWithItsCredentials()
    {
        await using FakeProxy fake = FakeProxy.Http(("bob", "pw"));
        using var factory = new UpstreamHttpClientFactory();

        ForwardOutcome outcome = await ForwardAsync(factory, Parse($"http://bob:pw@127.0.0.1:{fake.Port}"));

        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.Contains("http://upstream.test/v1/chat/completions", fake.Targets);
        Assert.Equal(["bob"], fake.Users);
    }

    [Fact]
    public async Task WrongProxyCredentialsAreAChannelLevelConnectionFailure()
    {
        await using FakeProxy fake = FakeProxy.Http(("bob", "pw"));
        using var factory = new UpstreamHttpClientFactory();

        ForwardOutcome outcome = await ForwardAsync(factory, Parse($"http://bob:wrong@127.0.0.1:{fake.Port}"));

        Assert.Equal(FailureClass.ConnectionFailure, outcome.FailureClass);
        Assert.Equal(407, outcome.HttpStatusCode);
        Assert.StartsWith($"经代理 127.0.0.1:{fake.Port}：", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableProxyIsNamedInTheError()
    {
        // 拿一个刚放掉的端口：上面没有人在听。
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var factory = new UpstreamHttpClientFactory();

        ForwardOutcome outcome = await ForwardAsync(factory, Parse($"socks5://127.0.0.1:{port}"));

        Assert.Equal(FailureClass.ConnectionFailure, outcome.FailureClass);
        Assert.StartsWith($"经代理 127.0.0.1:{port}：", outcome.ErrorMessage, StringComparison.Ordinal);
    }
}
