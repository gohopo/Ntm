using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// OpenAI Responses（切片 11）：/v1/responses 入站、Responses 格式的上游、previous_response_id 的落点（Q181–Q198、ADR-0016）。
/// </summary>
public sealed class ResponsesRelayTests
{
    private static string Created(string id = "resp_up1") =>
        Frame("response.created", 0, $"\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"gpt-real\",\"output\":[],\"usage\":null}}");

    private const string MessageAdded =
        "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}\n\n";

    private const string PartAdded =
        "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\",\"sequence_number\":2,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[]}}\n\n";

    private const string TextDelta =
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":3,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"你好\",\"logprobs\":[]}\n\n";

    private const string PartDone =
        "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\",\"sequence_number\":4,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"你好\",\"annotations\":[]}}\n\n";

    private static string Completed(string id = "resp_up1") =>
        Frame("response.completed", 5, $"\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-real\",\"output\":[],\"usage\":{{\"input_tokens\":30,\"input_tokens_details\":{{\"cached_tokens\":10}},\"output_tokens\":7,\"output_tokens_details\":{{\"reasoning_tokens\":0}},\"total_tokens\":37}}}}");

    private static string NonStreamAnswer(string id = "resp_up2") =>
        $"{{\"id\":\"{id}\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-real\"," +
        "\"output\":[{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"答\",\"annotations\":[]}]}]," +
        "\"usage\":{\"input_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens\":4,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":16}}";

    private static string Frame(string type, int sequence, string rest) =>
        $"event: {type}\ndata: {{\"type\":\"{type}\",\"sequence_number\":{sequence},{rest}}}\n\n";

    private static string[] Stream(string id = "resp_up1") => [Created(id), MessageAdded, PartAdded, TextDelta, PartDone, Completed(id)];

    private static Task<RelayHost> StartAsync(params (string Name, int Priority, string[] Secrets, ApiFormat Format)[] channels) =>
        RelayHost.StartWithFormatsAsync(channels);

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task AResponsesStreamPassesThroughUntouchedAndIsBilledFromResponseCompleted()
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-r", () => ScriptedUpstream.Sse(Stream()));

        using HttpResponseMessage response = await host.PostResponsesAsync(
            """{"model":"gpt-4o","stream":true,"store":true,"include":["reasoning.encrypted_content"],"input":"hi"}""");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Concat(Stream()), text);

        // 同格式透传：store / include 照客户端写的发（Q184），只换 model。
        JsonElement sent = Body(Assert.Single(host.Upstream.Calls).Body);
        Assert.True(sent.GetProperty("store").GetBoolean());
        Assert.Equal("gpt-4o", sent.GetProperty("model").GetString());
        Assert.EndsWith("/v1/responses", Assert.Single(host.Upstream.Calls).Url, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(20, record.InputTokens);
        Assert.Equal(10, record.CacheReadTokens);
        Assert.Equal(7, record.OutputTokens);
    }

    [Fact]
    public async Task APreviousResponseIdGoesBackToTheChannelAndKeyThatProducedIt()
    {
        await using RelayHost host = await StartAsync(
            ("a", 0, ["sk-a1", "sk-a2"], ApiFormat.OpenAiResponses),
            ("b", 0, ["sk-b"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-a1", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_from_a1")));
        host.Upstream.Script("sk-a2", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_from_a2")));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_from_b")));

        using HttpResponseMessage first = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"第一轮"}""");
        string firstId = Body(await first.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        string producer = Assert.Single(host.Upstream.Calls).Secret;

        // 连发几次接着这个 id 的请求：每次都必须回到同一把渠道密钥，不受轮询影响。
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage next = await host.PostResponsesAsync(
                $$"""{"model":"gpt-4o","previous_response_id":"{{firstId}}","input":"第{{i + 2}}轮"}""");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }

        Assert.All(host.Upstream.Calls, call => Assert.Equal(producer, call.Secret));
    }

    [Fact]
    public async Task AnUnknownPreviousResponseIdIsRejectedWithoutCallingAnyUpstream()
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));

        using HttpResponseMessage response = await host.PostResponsesAsync(
            """{"model":"gpt-4o","previous_response_id":"resp_never_seen","input":"hi"}""");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("previous_response_id", text, StringComparison.Ordinal);
        Assert.Empty(host.Upstream.Calls);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.ClientRequestError, record.BlockedBy);
    }

    [Fact]
    public async Task WhenTheProducingChannelFailsTheRequestFailsInsteadOfSwitching()
    {
        await using RelayHost host = await StartAsync(
            ("a", 10, ["sk-a"], ApiFormat.OpenAiResponses),
            ("b", 0, ["sk-b"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_a")));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_b")));

        using HttpResponseMessage first = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.InternalServerError, """{"error":{"message":"down"}}"""));
        using HttpResponseMessage next = await host.PostResponsesAsync(
            """{"model":"gpt-4o","previous_response_id":"resp_a","input":"again"}""");

        Assert.NotEqual(HttpStatusCode.OK, next.StatusCode);
        Assert.DoesNotContain(host.Upstream.Calls, call => call.Secret == "sk-b");
    }

    [Fact]
    public async Task OnlyResponsesUpstreamIdsSeenByAResponsesClientAreRemembered()
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-r", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer("resp_hidden")));

        // Chat 客户端经转换用了 Responses 上游：它看不到上游的 id，记下来也没用。
        using HttpResponseMessage chat = await host.PostAsync("""{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal(0, host.Services.GetRequiredService<ResponseLandingTable>().Count);

        using HttpResponseMessage direct = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal(1, host.Services.GetRequiredService<ResponseLandingTable>().Count);
    }

    [Fact]
    public async Task AChatClientOnAResponsesUpstreamGetsAChatAnswerAndTheUpstreamIsToldNotToStore()
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-r", () => ScriptedUpstream.Sse(Stream()));

        using HttpResponseMessage response = await host.PostAsync(
            """{"model":"gpt-4o","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"system","content":"规矩"},{"role":"user","content":"hi"}]}""");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"content\":\"你好\"", text, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"stop\"", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);

        (string _, string url, string body) = Assert.Single(host.Upstream.Calls);
        Assert.EndsWith("/v1/responses", url, StringComparison.Ordinal);
        JsonElement sent = Body(body);
        Assert.False(sent.GetProperty("store").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", sent.GetProperty("include")[0].GetString());
        Assert.Equal("规矩", sent.GetProperty("instructions").GetString());
        Assert.Equal(32, sent.GetProperty("prompt_cache_key").GetString()!.Length);
        Assert.False(sent.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task AnAnthropicClientOnAResponsesUpstreamGetsAMessage()
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-r", () => ScriptedUpstream.Json(HttpStatusCode.OK, NonStreamAnswer()));

        using HttpResponseMessage response = await host.PostMessagesAsync(
            """{"model":"claude-x","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""");
        JsonElement answer = Body(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("message", answer.GetProperty("type").GetString());
        Assert.Equal("答", answer.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(10, answer.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.Equal(2, answer.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt64());
    }

    [Fact]
    public async Task AResponsesClientOnAChatUpstreamGetsResponsesEvents()
    {
        await using RelayHost host = await StartAsync(("chat", 0, ["sk-c"], ApiFormat.OpenAiChat));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Sse(
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-chat\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"你好\"}}]}\n\n",
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-chat\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-chat\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\n",
            "data: [DONE]\n\n"));

        using HttpResponseMessage response = await host.PostResponsesAsync(
            """{"model":"gpt-4o","stream":true,"instructions":"规矩","input":"hi"}""");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("event: response.created\n", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"response.output_text.delta\"", text, StringComparison.Ordinal);
        Assert.Contains("event: response.completed\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", text, StringComparison.Ordinal);

        JsonElement sent = Body(Assert.Single(host.Upstream.Calls).Body);
        Assert.Equal("system", sent.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("规矩", sent.GetProperty("messages")[0].GetProperty("content").GetString());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(5, record.InputTokens);
        Assert.Equal(2, record.OutputTokens);
    }

    [Fact]
    public async Task AResponsesClientOnAnAnthropicUpstreamGetsADefaultMaxTokens()
    {
        await using RelayHost host = await StartAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-real\",\"content\":[{\"type\":\"text\",\"text\":\"答\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}"));

        using HttpResponseMessage response = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        JsonElement answer = Body(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("response", answer.GetProperty("object").GetString());
        Assert.StartsWith("resp_", answer.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("答", answer.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(4096, Body(Assert.Single(host.Upstream.Calls).Body).GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task StopSequencesRouteAroundResponsesChannels()
    {
        await using RelayHost host = await StartAsync(
            ("resp", 10, ["sk-r"], ApiFormat.OpenAiResponses),
            ("chat", 0, ["sk-c"], ApiFormat.OpenAiChat));
        host.Upstream.Script("sk-c", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"model\":\"gpt-chat\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}"));

        using HttpResponseMessage response = await host.PostAsync(
            """{"model":"gpt-4o","stop":["END"],"messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sk-c", Assert.Single(host.Upstream.Calls).Secret);
    }

    [Theory]
    [InlineData("""{"model":"gpt-4o","input":[{"type":"item_reference","id":"msg_1"}]}""")]
    [InlineData("""{"model":"gpt-4o","input":"hi","conversation":"conv_1","previous_response_id":"resp_1"}""")]
    [InlineData("""{"model":"gpt-4o","input":"hi","conversation":"conv_unknown"}""")]
    public async Task StatefulUsesTheGatewayCannotPlaceAre400(string json)
    {
        await using RelayHost host = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));

        using HttpResponseMessage response = await host.PostResponsesAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task AnUpstream400OnPassthroughIsEchoedButNotOnConversion()
    {
        const string upstreamError = """{"error":{"message":"bad tool schema","type":"invalid_request_error","param":"tools","code":"invalid_value"}}""";

        await using RelayHost passthrough = await StartAsync(("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        passthrough.Upstream.Script("sk-r", () => ScriptedUpstream.Json(HttpStatusCode.BadRequest, upstreamError));
        using HttpResponseMessage echoed = await passthrough.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        Assert.Equal(HttpStatusCode.BadRequest, echoed.StatusCode);
        Assert.Equal(upstreamError, await echoed.Content.ReadAsStringAsync());

        await using RelayHost converted = await StartAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        converted.Upstream.Script("sk-c", () => ScriptedUpstream.Json(
            HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad tool schema"}}"""));
        using HttpResponseMessage rewrapped = await converted.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        JsonElement body = Body(await rewrapped.Content.ReadAsStringAsync());

        // Anthropic 的错误体 Responses 客户端认不得：取出消息，按 Responses 的形状重新包。
        Assert.Equal(HttpStatusCode.BadRequest, rewrapped.StatusCode);
        Assert.True(body.TryGetProperty("error", out JsonElement error));
        Assert.Contains("bad tool schema", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(body.TryGetProperty("type", out _));
    }
}
