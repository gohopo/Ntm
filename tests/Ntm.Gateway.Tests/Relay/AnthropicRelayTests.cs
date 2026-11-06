using System.Net;
using System.Text.Json;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// Anthropic Messages 入站经 IR 转换到 OpenAI Chat 上游（切片 2）。选候选、重试、提交点、记录
/// 与 OpenAI 入站共用一副骨架；不同的只有给上游的字节、给客户端的形状和错误格式。
/// </summary>
public sealed class AnthropicRelayTests
{
    private const string Preamble = "data: {\"model\":\"gpt-4o-2024-11-20\",\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private const string ErrorFrame = "data: {\"error\":{\"message\":\"overloaded\",\"type\":\"server_error\"}}\n\n";

    private const string StreamBody =
        "{\"model\":\"claude-x\",\"max_tokens\":64,\"stream\":true,\"system\":\"be brief\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string NonStreamBody =
        "{\"model\":\"claude-x\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private static List<(string Name, JsonElement Data)> Events(string text)
    {
        var events = new List<(string, JsonElement)>();
        foreach (string frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] lines = frame.Split('\n');
            events.Add((lines[0]["event: ".Length..], JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone()));
        }

        return events;
    }

    private static JsonElement Error(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").Clone();

    [Fact]
    public async Task StreamIsConvertedAndRetriesBeforeCommitStayInvisible()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a1", "sk-a2"]));
        host.Upstream.Script(
            "sk-a1",
            () => ScriptedUpstream.Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}"));
        host.Upstream.Script("sk-a2", () => ScriptedUpstream.Sse(Preamble, Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostMessagesAsync(StreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = Events(text);
        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
        Assert.Equal("ok", events[2].Data.GetProperty("delta").GetProperty("text").GetString());

        // 消息 ID 跟着请求 ID 走，换候选不会变。
        string requestId = response.Headers.GetValues("x-ntm-request-id").Single();
        Assert.Equal(
            "msg_" + Guid.Parse(requestId).ToString("N"),
            events[0].Data.GetProperty("message").GetProperty("id").GetString());

        JsonElement usage = events[4].Data.GetProperty("usage");
        Assert.Equal(6, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("output_tokens").GetInt64());

        // 上游收到的是 OpenAI Chat 的请求：system 变成一条消息，自己写了 include_usage。
        (string _, string url, string body) = host.Upstream.Calls.Last();
        Assert.EndsWith("/v1/chat/completions", url, StringComparison.Ordinal);
        using JsonDocument upstream = JsonDocument.Parse(body);
        Assert.Equal("claude-x", upstream.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", upstream.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.True(upstream.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(ApiFormat.Anthropic, record.InboundApiFormat);
        Assert.Equal(2, record.AttemptCount);
        Assert.All(record.Attempts, a => Assert.False(a.Passthrough));
        Assert.Equal(ApiFormat.OpenAiChat, record.Attempts[1].UpstreamApiFormat);
        Assert.Equal(6, record.InputTokens);
        Assert.Equal(4, record.CacheReadTokens);
        Assert.Equal(2, record.OutputTokens);
    }

    [Fact]
    public async Task InStreamErrorBeforeCommitFallsBackToTheNextChannel()
    {
        await using RelayHost host = await RelayHost.StartAsync(("primary", 10, ["sk-p"]), ("backup", 0, ["sk-b"]));
        host.Upstream.Script("sk-p", () => ScriptedUpstream.Sse(Preamble, ErrorFrame));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Sse(Preamble, Content, Finish, Usage, Done));

        using HttpResponseMessage response = await host.PostMessagesAsync(StreamBody);
        string text = await response.Content.ReadAsStringAsync();

        // 第一个候选翻译出的 message_start 作废了：客户端只看到一条完整的消息。
        var names = Events(text).Select(e => e.Name).ToList();
        Assert.Equal(1, names.Count(n => n == "message_start"));
        Assert.Equal("message_stop", names[^1]);
        Assert.DoesNotContain("overloaded", text, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.UpstreamOverloaded, record.Attempts[0].FailureClass);
        Assert.False(record.Attempts[0].Committed);
        Assert.Equal(FailureClass.None, record.Attempts[1].FailureClass);
    }

    [Fact]
    public async Task NonStreamIsConvertedToAnAnthropicMessage()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"model\":\"gpt-4o-2024-11-20\",\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}"));

        using HttpResponseMessage response = await host.PostMessagesAsync(NonStreamBody);
        using JsonDocument message = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("message", message.RootElement.GetProperty("type").GetString());
        Assert.Equal("你好", message.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", message.RootElement.GetProperty("stop_reason").GetString());
        Assert.Equal(10, message.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt64());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(3, record.OutputTokens);
    }

    [Fact]
    public async Task MissingApiKeyIsAnAnthropicAuthenticationErrorAndNotRecorded()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));

        using HttpResponseMessage response = await host.PostMessagesAsync(NonStreamBody, token: null);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.Equal("error", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("authentication_error", Error(text).GetProperty("type").GetString());
        Assert.Empty(host.Upstream.Calls);
        Assert.Empty(await host.ReadRecordsAsync());
    }

    [Fact]
    public async Task InvalidRequestIsRejectedInAnthropicShapeWithoutCallingUpstream()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"claude-x\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"mystery\"}]}]}");
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement error = Error(text);
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Contains("mystery", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task UpstreamClientErrorIsRewrappedNotLeakedInOpenAiShape()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"max_tokens is too large\",\"type\":\"invalid_request_error\",\"param\":\"max_tokens\",\"code\":null}}"));

        using HttpResponseMessage response = await host.PostMessagesAsync(NonStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement error = Error(text);
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("max_tokens is too large", error.GetProperty("message").GetString());

        // OpenAI 的 param/code 字段不出现在 Anthropic 的错误里；请求本身有问题，不换候选。
        Assert.DoesNotContain("param", text, StringComparison.Ordinal);
        Assert.Equal(["sk-a"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task AllCandidatesFailingIsAnApiErrorWithoutUpstreamDetails()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script(
            "sk-a",
            () => ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom sk-a\"}}"));

        using HttpResponseMessage response = await host.PostMessagesAsync(NonStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("api_error", Error(text).GetProperty("type").GetString());
        Assert.DoesNotContain("boom", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-a", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnthropicUpstreamGetsThePassthroughBodyAndItsAnswerIsNotTouched()
    {
        // 同格式透传：只换 model，其余字节原样；上游的响应体也不重新组装（Q164、ADR-0002）。
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(("claude", 0, ["sk-c"], ApiFormat.Anthropic));
        host.Upstream.Script(
            "sk-c",
            () => ScriptedUpstream.Json(
                HttpStatusCode.OK,
                "{\"id\":\"msg_up\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-real\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\"," +
                "\"usage\":{\"input_tokens\":11,\"output_tokens\":3,\"cache_read_input_tokens\":4}}"));

        using HttpResponseMessage response = await host.PostMessagesAsync(NonStreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.Equal("msg_up", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("claude-real", body.RootElement.GetProperty("model").GetString());

        (string Secret, string Url, string Body) sent = Assert.Single(host.Upstream.Calls);
        Assert.Equal("sk-c", sent.Secret);
        using JsonDocument sentBody = JsonDocument.Parse(sent.Body);
        Assert.Equal("claude-x", sentBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(64, sentBody.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(sentBody.RootElement.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind != JsonValueKind.Array);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(11, record.InputTokens);
        Assert.Equal(4, record.CacheReadTokens);
        Assert.Equal(3, record.OutputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);
    }

    [Fact]
    public async Task EmptyStreamFromTheOnlyCandidateIsHandedOverAsAValidEmptyMessage()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(Preamble, Done));

        using HttpResponseMessage response = await host.PostMessagesAsync(StreamBody);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["message_start", "message_delta", "message_stop"], Events(text).Select(e => e.Name));

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Failed, record.Status);
    }
}
