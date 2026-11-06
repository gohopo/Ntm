using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Live;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// Anthropic 格式的上游（切片 10）：Anthropic 入站透传，OpenAI Chat 入站经 IR 转换（Q163–Q180）。
/// </summary>
public sealed class AnthropicUpstreamRelayTests
{
    private const string MessageStart =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\"," +
        "\"model\":\"claude-real\",\"content\":[],\"usage\":{\"input_tokens\":20,\"cache_read_input_tokens\":5,\"output_tokens\":1}}}\n\n";

    private const string Ping = "event: ping\ndata: {\"type\":\"ping\"}\n\n";

    private const string ThinkingStart =
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n";

    private const string ThinkingDelta =
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"想想\"}}\n\n";

    private const string TextStart =
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n";

    private const string TextDelta =
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}\n\n";

    private const string BlockStop = "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n";

    private const string MessageDelta =
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":9}}\n\n";

    private const string MessageStop = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private const string OverloadedEvent =
        "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

    private const string AnthropicStreamBody =
        "{\"model\":\"claude-x\",\"max_tokens\":64,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string NonStreamAnswer =
        "{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-real\"," +
        "\"content\":[{\"type\":\"text\",\"text\":\"答\"}],\"stop_reason\":\"end_turn\"," +
        "\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":2,\"cache_creation_input_tokens\":6,\"output_tokens\":4}}";

    private static readonly string[] AnthropicStream =
        [MessageStart, Ping, ThinkingStart, ThinkingDelta, BlockStop, TextStart, TextDelta, BlockStop, MessageDelta, MessageStop];

    private static Task<RelayHost> StartAnthropicAsync(params (string Name, int Priority, string[] Secrets)[] channels) =>
        RelayHost.StartWithFormatsAsync([.. channels.Select(c => (c.Name, c.Priority, c.Secrets, ApiFormat.Anthropic))]);

    private static List<JsonElement> Chunks(string sse) =>
        [.. sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame["data: ".Length..])
            .Where(data => data != "[DONE]")
            .Select(data => JsonDocument.Parse(data).RootElement.Clone())];

    [Fact]
    public async Task AnAnthropicStreamPassesThroughUntouchedAndIsBilledFromItsOwnUsage()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Sse(AnthropicStream));

        using HttpResponseMessage response = await host.PostMessagesAsync(AnthropicStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Concat(AnthropicStream), text);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(20, record.InputTokens);
        Assert.Equal(5, record.CacheReadTokens);
        Assert.Equal(9, record.OutputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);

        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.True(attempt.Passthrough);
        Assert.True(attempt.SawThinking);
        Assert.NotNull(attempt.FirstContentEventAt);
    }

    [Fact]
    public async Task AnOverloadedEventBeforeContentFailsOverInvisibly()
    {
        await using RelayHost host = await StartAnthropicAsync(("a", 10, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(MessageStart, Ping, OverloadedEvent));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Sse(AnthropicStream));

        using HttpResponseMessage response = await host.PostMessagesAsync(AnthropicStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("overloaded_error", text, StringComparison.Ordinal);
        Assert.Equal(["sk-a", "sk-b"], host.Upstream.SecretsTried);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.UpstreamOverloaded, record.Attempts[0].FailureClass);
        Assert.Equal(FailureClass.None, record.Attempts[1].FailureClass);
    }

    [Fact]
    public async Task TheClientsVersionAndBetaHeadersAreForwardedOnlyBetweenTwoAnthropics()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(
                "{\"model\":\"claude-x\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", RelayHost.ApiKeySecret);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2024-01-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "prompt-caching-2024-07-31");
        using HttpResponseMessage response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyDictionary<string, string> headers = Assert.Single(host.Upstream.Headers);
        Assert.Equal("2024-01-01", headers["anthropic-version"]);
        Assert.Equal("prompt-caching-2024-07-31", headers["anthropic-beta"]);
        Assert.Equal("sk-c", headers["x-api-key"]);
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AnOpenAiStreamIsConvertedBothWaysAndTheGatewayFillsMaxTokens()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Sse(AnthropicStream));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"claude-x\",\"stream\":true,\"stream_options\":{\"include_usage\":true}," +
            "\"messages\":[{\"role\":\"system\",\"content\":\"be brief\"},{\"role\":\"user\",\"content\":\"hi\"}]}");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
        List<JsonElement> chunks = Chunks(text);
        Assert.Contains(chunks, c => c.GetProperty("choices").GetArrayLength() > 0
            && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out JsonElement t)
            && t.GetString() == "你好");
        Assert.Equal(25, chunks.Last().GetProperty("usage").GetProperty("prompt_tokens").GetInt64());

        // 上游收到的是 Anthropic 请求：model 换成上游名，system 拎到顶层，max_tokens 由网关补上（Q172）。
        JsonElement sent = JsonDocument.Parse(Assert.Single(host.Upstream.Calls).Body).RootElement;
        Assert.Equal("claude-x", sent.GetProperty("model").GetString());
        Assert.Equal("be brief", sent.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal(4096, sent.GetProperty("max_tokens").GetInt64());
        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.False(sent.TryGetProperty("stream_options", out _));

        IReadOnlyDictionary<string, string> headers = Assert.Single(host.Upstream.Headers);
        Assert.Equal(ApiFormatAuth.AnthropicDefaultVersion, headers["anthropic-version"]);
        Assert.False(headers.ContainsKey("anthropic-beta"));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(9, record.OutputTokens);
        Assert.False(record.Attempts[0].Passthrough);
    }

    [Fact]
    public async Task AnOpenAiNonStreamRequestGetsAChatCompletionBack()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"claude-x\",\"max_tokens\":32,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("答", root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", root.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(18, root.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());

        JsonElement sent = JsonDocument.Parse(Assert.Single(host.Upstream.Calls).Body).RootElement;
        Assert.Equal(32, sent.GetProperty("max_tokens").GetInt64());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(6, record.CacheWrite5mTokens);
    }

    [Fact]
    public async Task ARequestAnthropicCannotExpressSkipsItsChannelsAndSaysWhy()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"claude-x\",\"n\":2,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("渠道 claude", text, StringComparison.Ordinal);
        Assert.Empty(host.Upstream.Calls);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Empty(record.Attempts);
        Assert.StartsWith("渠道 claude：", record.BlockDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrepresentableRequestStillReachesAnOpenAiChannel()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("claude", 10, ["sk-c"], ApiFormat.Anthropic),
            ("openai", 0, ["sk-o"], ApiFormat.OpenAiChat));
        host.Upstream.Script(
            "sk-o",
            () => ScriptedUpstream.Json(
                HttpStatusCode.OK,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"a\"},\"finish_reason\":\"stop\"}," +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"b\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}"));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"claude-x\",\"n\":2,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-o"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task InterruptedMidStreamTheInputIsTheUpstreamsAndOnlyTheOutputIsEstimated()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        host.Upstream.ScriptHanging("sk-c", MessageStart, TextStart, TextDelta);

        Task<HttpResponseMessage> pending = host.PostMessagesAsync(AnthropicStreamBody);
        InFlightRegistry registry = host.Services.GetRequiredService<InFlightRegistry>();
        InFlightRequest? running = null;
        for (int i = 0; i < 500 && running is null; i++)
        {
            running = registry.Snapshot().FirstOrDefault(r => r.CommittedAt is not null);
            if (running is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(running);
        Assert.True(registry.TryInterrupt(running.RequestId));
        using HttpResponseMessage response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(FailureClass.Interrupted, attempt.FailureClass);

        // message_start 里的输入是真数，直接用；只有输出是按看到的字符估的（Q177）。
        Assert.Equal(UsageSource.GatewayEstimated, attempt.UsageSource);
        Assert.Equal(20, attempt.InputTokens);
        Assert.Equal(5, attempt.CacheReadTokens);
        Assert.True(attempt.OutputTokens > 0);
    }

    [Fact]
    public async Task ABearerChannelSendsAuthorizationInsteadOfXApiKey()
    {
        await using RelayHost host = await StartAnthropicAsync(("claude", 0, ["sk-c"]));
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            await db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.AnthropicBearerAuth, true));
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer));

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"claude-x\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyDictionary<string, string> headers = Assert.Single(host.Upstream.Headers);
        Assert.Equal("Bearer sk-c", headers["Authorization"]);
        Assert.False(headers.ContainsKey("x-api-key"));
        Assert.Equal(ApiFormatAuth.AnthropicDefaultVersion, headers["anthropic-version"]);
    }
}
