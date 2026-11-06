using System.Net;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 中继的选候选与重试（切片 1）：只在提交点之前换候选（ADR-0008），
/// 密钥级失败换同渠道的下一把密钥，其余失败换下一个渠道（Q28），每次尝试都落一条 Attempt。
/// </summary>
public sealed class ChatCompletionsRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Preamble = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";
    private const string Usage =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private const string StreamBody = "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}";
    private const string NonStreamBody = "{\"model\":\"gpt-4o\",\"messages\":[]}";

    private static HttpResponseMessage RateLimited() =>
        ScriptedUpstream.Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}");

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    [Fact]
    public async Task RateLimitedKeyFallsBackToTheNextKeyOfTheSameChannel()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a1", "sk-a2"]));
        host.Upstream.Script("sk-a1", RateLimited);
        host.Upstream.Script("sk-a2", () => ScriptedUpstream.Sse(Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 第一次 429 对客户端不可见；我们注入的 usage 帧被剔掉。
        Assert.Equal(Content + Finish + Done, text);
        Assert.Equal(["sk-a1", "sk-a2"], host.Upstream.SecretsTried);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(2, record.AttemptCount);
        Assert.Equal(FailureClass.RateLimited, record.Attempts[0].FailureClass);
        Assert.Equal(FailureClass.None, record.Attempts[1].FailureClass);
        Assert.True(record.Attempts[1].Committed);
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(2, record.OutputTokens);
    }

    [Fact]
    public async Task ChannelLevelFailureSkipsTheRestOfThatChannelsKeys()
    {
        await using RelayHost host = await RelayHost.StartAsync(
            ("primary", 10, ["sk-p1", "sk-p2"]),
            ("backup", 0, ["sk-b1"]));
        host.Upstream.Script("sk-p1", ServerError);
        host.Upstream.Script("sk-p2", () => ScriptedUpstream.Sse(Content, Done));
        host.Upstream.Script("sk-b1", () => ScriptedUpstream.Sse(Content, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 5xx 是渠道级的：同渠道另一把密钥救不了，直接去下一个渠道。高优先级先试。
        Assert.Equal(["sk-p1", "sk-b1"], host.Upstream.SecretsTried);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(["primary", "backup"], record.Attempts.Select(a => a.ChannelName));
        Assert.Equal(FailureClass.UpstreamServerError, record.Attempts[0].FailureClass);
    }

    [Fact]
    public async Task ClientRequestErrorIsPassedThroughAndNotRetried()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a"]), ("b", 0, ["sk-b"]));
        const string upstreamError = "{\"error\":{\"message\":\"messages is required\",\"param\":\"messages\"}}";
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.BadRequest, upstreamError));

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);

        // 请求本身有问题，换渠道也一样：上游的错误原样返回（Q28）。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(upstreamError, await response.Content.ReadAsStringAsync());
        Assert.Equal(["sk-a"], host.Upstream.SecretsTried);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Failed, record.Status);
        Assert.Equal(400, record.HttpStatusCode);
    }

    [Fact]
    public async Task WhenEveryCandidateFailsTheClientGetsAGatewayErrorWithoutUpstreamDetails()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(
            HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: sk-a\"}}"));

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        // 上游认证失败是网关自己的凭证问题，不把上游的话（还带着密钥片段）透给客户端。
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("sk-a", text, StringComparison.Ordinal);
        Assert.Contains("upstream_failed", text, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.AuthFailure, Assert.Single(record.Attempts).FailureClass);
    }

    [Fact]
    public async Task EmptyStreamIsDiscardedAndTheNextCandidateServesTheClient()
    {
        await using RelayHost host = await RelayHost.StartAsync(("empty", 10, ["sk-e"]), ("good", 0, ["sk-g"]));
        host.Upstream.Script("sk-e", () => ScriptedUpstream.Sse(Preamble, Done));
        host.Upstream.Script("sk-g", () => ScriptedUpstream.Sse(Preamble, Content, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        string text = await response.Content.ReadAsStringAsync();

        // 第一个上游的序幕没有到达客户端：客户端只看到一份完整的流。
        Assert.Equal(Preamble + Content + Done, text);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(2, record.AttemptCount);
        Assert.False(record.Attempts[0].Committed);
        Assert.True(record.Attempts[1].Committed);
    }

    [Fact]
    public async Task OnlyCandidateEndingEmptyIsHandedOverAsIs()
    {
        await using RelayHost host = await RelayHost.StartAsync(("empty", 0, ["sk-e"]));
        host.Upstream.Script("sk-e", () => ScriptedUpstream.Sse(Preamble, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);

        // 没有候选了：把上游给的原样交出去，不替它编错误。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Preamble + Done, await response.Content.ReadAsStringAsync());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Failed, record.Status);
    }

    [Fact]
    public async Task UpstreamGetsTheChannelKeyNeverTheClientApiKey()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-upstream"]));
        host.Upstream.Script("sk-upstream", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}"));

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(ChatRelay.RequestIdHeader));

        (string secret, string url, string body) = Assert.Single(host.Upstream.Calls);
        Assert.Equal("sk-upstream", secret);
        Assert.Equal("https://a.example/v1/chat/completions", url);

        // 非流式不注入 include_usage，字节原样。
        Assert.Equal(NonStreamBody, body);
        Assert.DoesNotContain(RelayHost.ApiKeySecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingRequestGetsIncludeUsageInjected()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Content, Done));

        using HttpResponseMessage response = await host.PostAsync(StreamBody);
        await response.Content.ReadAsStringAsync();

        (_, _, string body) = Assert.Single(host.Upstream.Calls);
        Assert.Equal(
            "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[],\"stream_options\":{\"include_usage\":true}}",
            body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ntm-wrong")]
    public async Task UnauthenticatedRequestsAreRejectedAndNotRecorded(string? token)
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Upstream.Calls);

        // 未认证的流量不写盘，否则就成了用磁盘做的拒绝服务。
        Assert.Empty(await host.ReadRecordsAsync());
    }

    [Fact]
    public async Task NoCandidateMeansServiceUnavailable()
    {
        await using RelayHost host = await RelayHost.StartAsync();

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Empty(record.Attempts);
        Assert.Equal(ApiFormat.OpenAiChat, record.InboundApiFormat);
    }

    [Fact]
    public async Task AttemptsAreCappedAtFive()
    {
        string[] secrets = [.. Enumerable.Range(1, 7).Select(i => "sk-" + i)];
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, secrets));
        foreach (string secret in secrets)
        {
            host.Upstream.Script(secret, RateLimited);
        }

        using HttpResponseMessage response = await host.PostAsync(NonStreamBody);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ChatRelay.MaxAttempts, host.Upstream.Calls.Count);
    }
}
