using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Routing;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>Anthropic 配套：自动缓存断点的渠道开关、count_tokens（Q240–Q255，ADR-0019）。</summary>
public sealed class AnthropicCompanionRelayTests
{
    private const string NonStreamAnswer =
        "{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-real\"," +
        "\"content\":[{\"type\":\"text\",\"text\":\"答\"}],\"stop_reason\":\"end_turn\"," +
        "\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":2,\"cache_creation_input_tokens\":6,\"output_tokens\":4}}";

    private const string ChatBody =
        "{\"model\":\"claude-x\",\"messages\":[{\"role\":\"system\",\"content\":\"规则\"},{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string CountBody =
        "{\"model\":\"claude-x\",\"max_tokens\":64,\"stream\":true,\"temperature\":0.5,\"system\":\"规则\"," +
        "\"messages\":[{\"role\":\"user\",\"content\":\"hello world, hello world\"}]}";

    private static Task<RelayHost> StartAnthropicAsync(params (string Name, int Priority, string[] Secrets)[] channels) =>
        RelayHost.StartWithFormatsAsync([.. channels.Select(c => (c.Name, c.Priority, c.Secrets, ApiFormat.Anthropic))]);

    private static async Task UpdateChannelsAsync(RelayHost host, Func<ConfigDbContext, Task> update)
    {
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await update(scope.ServiceProvider.GetRequiredService<ConfigDbContext>());
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
    }

    private static int CacheMarks(string body)
    {
        int count = 0;
        int at = 0;
        while ((at = body.IndexOf("\"cache_control\"", at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at++;
        }

        return count;
    }

    private static Task<HttpResponseMessage> CountAsync(RelayHost host, string json, string? token = RelayHost.ApiKeySecret) =>
        host.PostToAsync("/v1/messages/count_tokens", json, token);

    [Fact]
    public async Task OpenAiRequestsToAnAnthropicChannelGetAutoBreakpointsByDefault()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));

        using HttpResponseMessage response = await host.PostAsync(ChatBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sent = Assert.Single(host.Upstream.Calls).Body;
        using JsonDocument document = JsonDocument.Parse(sent);
        Assert.True(document.RootElement.GetProperty("system")[0].TryGetProperty("cache_control", out _));
        Assert.True(document.RootElement.GetProperty("messages")[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public async Task TurningTheSwitchOffStopsAutoBreakpointsButKeepsClientOnes()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        await UpdateChannelsAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.AutoCacheBreakpoints, false)));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));

        using (HttpResponseMessage plain = await host.PostAsync(ChatBody))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }

        using (HttpResponseMessage marked = await host.PostAsync(
            "{\"model\":\"claude-x\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\",\"cache_control\":{\"type\":\"ephemeral\"}}]}]}"))
        {
            Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        }

        string[] bodies = [.. host.Upstream.Calls.Select(c => c.Body)];
        Assert.Equal(0, CacheMarks(bodies[0]));
        Assert.Equal(1, CacheMarks(bodies[1]));
    }

    [Fact]
    public async Task AnthropicPassthroughIsNeverGivenBreakpoints()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));
        const string Body = "{\"model\":\"claude-x\",\"max_tokens\":64,\"system\":\"规则\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        using HttpResponseMessage response = await host.PostMessagesAsync(Body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, CacheMarks(Assert.Single(host.Upstream.Calls).Body));
    }

    [Fact]
    public async Task CountTokensAsksTheAnthropicChannelWithOnlyTheCountFields()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, "{\"input_tokens\":321}"));

        using HttpResponseMessage response = await CountAsync(host, CountBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream", response.Headers.GetValues(TokenCounter.SourceHeader).Single());
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(321, answer.RootElement.GetProperty("input_tokens").GetInt64());

        (string secret, string url, string body) = Assert.Single(host.Upstream.Calls);
        Assert.Equal("sk-c", secret);
        Assert.EndsWith("/v1/messages/count_tokens", url, StringComparison.Ordinal);
        using JsonDocument sent = JsonDocument.Parse(body);
        string[] fields = [.. sent.RootElement.EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["model", "messages", "system"], fields);

        // count_tokens 不落请求记录（Q255）。
        Assert.Empty(await host.ReadRecordsAsync());
    }

    [Fact]
    public async Task AnUpstreamWithoutCountTokensIsRememberedAndEstimatedLocally()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.NotFound, "{\"error\":\"no route\"}"));

        using HttpResponseMessage first = await CountAsync(host, CountBody);
        using HttpResponseMessage second = await CountAsync(host, CountBody);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("estimated", first.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.Equal("estimated", second.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.Single(host.Upstream.Calls);

        using JsonDocument answer = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(answer.RootElement.GetProperty("input_tokens").GetInt64() > 0);

        // 不冷却。
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task OtherUpstreamFailuresFallBackToTheEstimateWithoutRemembering()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"));

        using HttpResponseMessage first = await CountAsync(host, CountBody);
        using HttpResponseMessage second = await CountAsync(host, CountBody);

        Assert.Equal("estimated", first.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.Equal(2, host.Upstream.Calls.Count);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task OpenAiChannelsAreNotAskedAndTheEstimateLeansHigh()
    {
        await using RelayHost host = await RelayHost.StartAsync(("openai", 0, ["sk-o"]));

        using HttpResponseMessage response = await CountAsync(host, CountBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("estimated", response.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.Empty(host.Upstream.Calls);
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TokenCounter.Estimate(System.Text.Encoding.UTF8.GetBytes(CountBody)), answer.RootElement.GetProperty("input_tokens").GetInt64());
    }

    [Fact]
    public async Task CountTokensChecksTheApiKeyAndTheModel()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));

        using HttpResponseMessage anonymous = await CountAsync(host, CountBody, token: null);
        using HttpResponseMessage unknown = await CountAsync(host, CountBody.Replace("claude-x", "nope", StringComparison.Ordinal));
        using HttpResponseMessage broken = await CountAsync(host, "{\"model\":\"claude-x\"}");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("\"type\":\"error\"", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task ACustomMessagesPathGetsCountTokensAppendedAndOtherPathsAreEstimated()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        await UpdateChannelsAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.Path, "/api/v3/messages?beta=1")));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, "{\"input_tokens\":7}"));

        using HttpResponseMessage response = await CountAsync(host, CountBody);

        Assert.Equal("upstream", response.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.EndsWith("/api/v3/messages/count_tokens?beta=1", Assert.Single(host.Upstream.Calls).Url, StringComparison.Ordinal);

        await UpdateChannelsAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.Path, "/chat")));
        using HttpResponseMessage custom = await CountAsync(host, CountBody);
        Assert.Equal("estimated", custom.Headers.GetValues(TokenCounter.SourceHeader).Single());
        Assert.Single(host.Upstream.Calls);
    }
}
