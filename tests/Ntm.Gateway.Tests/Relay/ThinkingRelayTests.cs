using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>推理强度和思考参数跨格式互转，以及渠道的「思考参数写法」（Q288–Q299，ADR-0021）。</summary>
public sealed class ThinkingRelayTests
{
    private const string AnthropicAnswer =
        "{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-real\"," +
        "\"content\":[{\"type\":\"thinking\",\"thinking\":\"想\",\"signature\":\"sig\"},{\"type\":\"text\",\"text\":\"答\"}]," +
        "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}";

    private const string ChatAnswer =
        "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"答\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4}}";

    private static async Task SetStyleAsync(RelayHost host, AnthropicThinkingStyle style)
    {
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Channels
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.ThinkingStyle, style));
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
    }

    [Fact]
    public async Task OpenAiReasoningEffortReachesAnAnthropicChannelAsThinking()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, AnthropicAnswer));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"claude-x\",\"reasoning_effort\":\"medium\",\"temperature\":0.7,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument sent = JsonDocument.Parse(Assert.Single(host.Upstream.Calls).Body);
        JsonElement root = sent.RootElement;
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(8192, root.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
        Assert.Equal(8192 + AnthropicThinking.BudgetReplyAllowance, root.GetProperty("max_tokens").GetInt64());
        Assert.False(root.TryGetProperty("temperature", out _));

        using JsonDocument reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("答", reply.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task TheChannelStyleSwitchesToAdaptiveAndPlainRequestsStayUntouched()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        await SetStyleAsync(host, AnthropicThinkingStyle.Adaptive);
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, AnthropicAnswer));

        using (HttpResponseMessage first = await host.PostResponsesAsync(
            "{\"model\":\"claude-x\",\"reasoning\":{\"effort\":\"high\"},\"input\":\"hi\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // 没有强度的请求：基于预算的渠道照旧（补 4096）、不写 thinking。
        await SetStyleAsync(host, AnthropicThinkingStyle.Budget);
        using (HttpResponseMessage second = await host.PostAsync(
            "{\"model\":\"claude-x\",\"temperature\":0.4,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        JsonElement[] bodies = [.. host.Upstream.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement)];
        Assert.Equal("adaptive", bodies[0].GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", bodies[0].GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(AnthropicThinking.AdaptiveDefaultMaxTokens, bodies[0].GetProperty("max_tokens").GetInt64());

        Assert.False(bodies[1].TryGetProperty("thinking", out _));
        Assert.Equal(4096, bodies[1].GetProperty("max_tokens").GetInt64());
        Assert.Equal(0.4, bodies[1].GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task AnthropicThinkingReachesAnOpenAiChannelAsReasoningEffort()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("openai", 0, ["sk-o"], ApiFormat.OpenAiChat));
        host.Upstream.Script("sk-o", () => ScriptedUpstream.Json(HttpStatusCode.OK, ChatAnswer));

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"gpt-4o\",\"max_tokens\":30000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":20000}," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument sent = JsonDocument.Parse(Assert.Single(host.Upstream.Calls).Body);
        Assert.Equal("high", sent.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task AnthropicPassthroughKeepsThinkingAsIs()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        await SetStyleAsync(host, AnthropicThinkingStyle.Adaptive);
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, AnthropicAnswer));

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"claude-x\",\"max_tokens\":9000,\"temperature\":1,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":5000}," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument sent = JsonDocument.Parse(Assert.Single(host.Upstream.Calls).Body);
        Assert.Equal("enabled", sent.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(5000, sent.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
        Assert.Equal(1, sent.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task ARejectedThinkingStyleIsRetriedOnceWithTheOtherStyleAndRemembered()
    {
        // claude-x 认不出版本，自动写法先写 enabled；上游说这个模型只接受 adaptive（Q315）。
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        host.Upstream.Script("sk-c", (_, _, body) => body.Contains("\"enabled\"", StringComparison.Ordinal)
            ? ScriptedUpstream.Json(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"\"thinking.type.enabled\" is not supported for this model. Use \"thinking.type.adaptive\" and \"output_config.effort\" to control thinking behavior."}}""")
            : ScriptedUpstream.Json(HttpStatusCode.OK, AnthropicAnswer));

        const string Request = "{\"model\":\"claude-x\",\"reasoning_effort\":\"high\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        using (HttpResponseMessage first = await host.PostAsync(Request))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PostAsync(Request))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        string[] types = [.. host.Upstream.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("thinking").GetProperty("type").GetString()!)];
        Assert.Equal(["enabled", "adaptive", "adaptive"], types);
        Assert.Equal(AnthropicThinkingStyle.Adaptive, host.Services.GetRequiredService<Gateway.Relay.ThinkingStyleMemory>()
            .Find(host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single(), "claude-x"));

        List<Gateway.Traffic.RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal([2, 1], records.Select(r => r.Attempts.Count));
        Assert.Equal(Gateway.Traffic.FailureClass.ClientRequestError, records[0].Attempts[0].FailureClass);
    }

    [Fact]
    public async Task OtherBadRequestsAndFixedStylesAreNotRetried()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"\"thinking.type.enabled\" is not supported for this model."}}"""));

        // 渠道固定成预算写法：上游拒了也是管理员的选择，原样回给客户端。
        await SetStyleAsync(host, AnthropicThinkingStyle.Budget);
        using (HttpResponseMessage fixedStyle = await host.PostAsync("{\"model\":\"claude-x\",\"reasoning_effort\":\"high\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, fixedStyle.StatusCode);
        }

        // 自动写法、但这次网关没写思考：拒绝和网关无关，不重发。
        await SetStyleAsync(host, AnthropicThinkingStyle.Auto);
        using (HttpResponseMessage plain = await host.PostAsync("{\"model\":\"claude-x\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        }

        Assert.Equal(2, host.Upstream.Calls.Count);
    }
}
