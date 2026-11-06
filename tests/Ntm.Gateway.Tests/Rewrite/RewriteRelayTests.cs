using System.Net;
using System.Text.Json;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Rewrite;
using Ntm.Gateway.Tests.Relay;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Rewrite;

/// <summary>
/// 渠道改写规则落到真正发往上游的请求上（Q149/Q150）：规则先跑，网关自己的模型名替换与
/// include_usage 注入后跑，所以规则改不掉选路和计费依赖的字段。
/// </summary>
public sealed class RewriteRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Done = "data: [DONE]\n\n";

    private static HttpResponseMessage Ok() => ScriptedUpstream.Sse(Content, Done);

    private static HttpResponseMessage OkJson() => ScriptedUpstream.Json(
        HttpStatusCode.OK,
        "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}");

    private static ChannelRewriteRule Rule(RewriteTarget target, RewriteOperation op, string path, string? value = null) =>
        new() { Target = target, Operation = op, Path = path, Value = value };

    private static Action<ConfigDbContext, IReadOnlyList<Channel>> Configure(
        IReadOnlyList<ChannelRewriteRule> rules,
        ApiFormat channelFormat = ApiFormat.OpenAiChat) =>
        (db, channels) =>
        {
            foreach (Channel channel in channels)
            {
                channel.ApiFormat = channelFormat;
                foreach (string model in RelayHost.Models)
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                }

                channel.RewriteRules.AddRange(rules);
            }

            foreach (string model in RelayHost.Models)
            {
                db.PublishedModels.Add(new PublishedModel
                {
                    Name = model,
                    Enabled = true,
                    Rules = { new MatchRule { Kind = MatchKind.Exact, Pattern = model, Ordinal = 0 } },
                });
            }
        };

    [Fact]
    public async Task BodyRulesReachTheUpstreamRequestAndTheModelIsStillReplaced()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure([
                Rule(RewriteTarget.Body, RewriteOperation.Set, "thinking.type", "\"disabled\""),
                Rule(RewriteTarget.Body, RewriteOperation.Remove, "top_k"),
                Rule(RewriteTarget.Body, RewriteOperation.Default, "temperature", "0.2"),
            ]));
        host.Upstream.Script("sk-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"gpt-4o\",\"messages\":[],\"top_k\":5}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        (_, _, string body) = Assert.Single(host.Upstream.Calls);
        using JsonDocument sent = JsonDocument.Parse(body);
        Assert.Equal("gpt-4o", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("disabled", sent.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(sent.RootElement.TryGetProperty("top_k", out _));
        Assert.Equal(0.2, sent.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task GatewayInjectionStillHappensOnTopOfTheRules()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure([Rule(RewriteTarget.Body, RewriteOperation.Set, "top_p", "0.9")]));
        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        (_, _, string body) = Assert.Single(host.Upstream.Calls);
        using JsonDocument sent = JsonDocument.Parse(body);
        Assert.Equal(0.9, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());

        // 客户端没要用量，网关注入的 include_usage 还在（ADR-0002）。
        Assert.True(sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task HeaderRulesReachTheUpstreamRequestButTheAuthHeaderStands()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure([
                Rule(RewriteTarget.Header, RewriteOperation.Set, "x-ntm-test", "on"),
                Rule(RewriteTarget.Header, RewriteOperation.Default, "x-ntm-default", "fresh"),
            ]));
        host.Upstream.Script("sk-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync("{\"model\":\"gpt-4o\",\"messages\":[]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(host.Upstream.Headers.TryDequeue(out IReadOnlyDictionary<string, string>? headers));
        Assert.Equal("on", headers!["x-ntm-test"]);
        Assert.Equal("fresh", headers["x-ntm-default"]);
        Assert.Equal("Bearer sk-a", headers["Authorization"]);
    }

    [Fact]
    public async Task RulesApplyToTheConvertedBodyWhenTheClientSpeaksAnthropic()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Configure([Rule(RewriteTarget.Body, RewriteOperation.Set, "top_p", "0.5")]));
        host.Upstream.Script("sk-a", OkJson);

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"claude-x\",\"max_tokens\":16,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        (_, _, string body) = Assert.Single(host.Upstream.Calls);
        using JsonDocument sent = JsonDocument.Parse(body);

        // 转换路径写出来的是 OpenAI Chat 的字节，规则按上游格式写（Q150）。
        Assert.Equal(0.5, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("claude-x", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(16, sent.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ChannelsWithDifferentRulesDoNotShareACachedBody()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-a"]), ("b", 0, ["sk-b"])],
            (db, channels) =>
            {
                Configure([])(db, channels);
                channels[0].RewriteRules.Add(Rule(RewriteTarget.Body, RewriteOperation.Set, "top_p", "0.1"));
                channels[1].RewriteRules.Add(Rule(RewriteTarget.Body, RewriteOperation.Set, "top_p", "0.9"));
            });

        // 高优先级的 a 先被试，失败后换到 b：两个渠道的规则必须各自生效（Q149）。
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}"));
        host.Upstream.Script("sk-b", OkJson);

        using HttpResponseMessage response = await host.PostAsync("{\"model\":\"gpt-4o\",\"messages\":[]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<(string Secret, string Url, string Body)> calls = [.. host.Upstream.Calls];
        Assert.Equal(2, calls.Count);
        Assert.Equal(0.1, JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(0.9, JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("top_p").GetDouble());
    }
}
