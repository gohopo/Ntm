using System.Net;
using System.Text.Json;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Forwarding;

/// <summary>
/// 转换路径：Anthropic 客户端，OpenAI Chat 上游。提交点、换候选、用量与透传一致（ADR-0001、ADR-0008），
/// 只是交给客户端的字节换成了翻译后的 Anthropic 形状。
/// </summary>
public sealed class ConvertedForwarderTests
{
    private const string PreambleFrame = "data: {\"model\":\"gpt-4o-2024-11-20\",\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";
    private const string ContentFrame = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
    private const string FinishFrame = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string UsageFrame =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n";
    private const string DoneFrame = "data: [DONE]\n\n";
    private const string ErrorFrame = "data: {\"error\":{\"message\":\"overloaded\",\"type\":\"server_error\"}}\n\n";

    private static ForwardRequest Request(bool stream) => new()
    {
        RequestUri = new Uri("https://api.openai.com/v1/chat/completions"),
        Secret = "sk-test",
        UpstreamApiFormat = ApiFormat.OpenAiChat,
        InboundApiFormat = ApiFormat.Anthropic,
        Body = "{\"model\":\"gpt-4o\"}"u8.ToArray(),
        IsStream = stream,
        Conversion = new ResponseConversion { MessageId = "msg_test", FallbackModel = "claude-x" },
    };

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

    [Fact]
    public async Task StreamIsTranslatedAndCommittedAtTheFirstContentEvent()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        Task<ForwardOutcome> forwarding = forwarder.ForwardAsync(Request(stream: true), committer, CancellationToken.None);

        upstream.Write(PreambleFrame);

        // 等转发器把序幕读进缓冲。固定睡 50 ms 在整套测试并行跑满时不够，会偶发失败。
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (committer.BufferedBytes == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        // 只有序幕：message_start 已翻译出来，但留在缓冲里，客户端还什么都没收到。
        Assert.False(recorder.HasStarted);
        Assert.True(committer.BufferedBytes > 0);

        upstream.Write(ContentFrame);
        upstream.Write(FinishFrame);
        upstream.Write(UsageFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();
        ForwardOutcome outcome = await forwarding;

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Committed);
        Assert.Equal(8, outcome.Usage.InputTokens);
        Assert.Equal(4, outcome.Usage.CacheReadTokens);
        Assert.Equal(5, outcome.Usage.OutputTokens);

        var events = Events(recorder.WrittenText);
        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
        Assert.Equal("msg_test", events[0].Data.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal("gpt-4o-2024-11-20", events[0].Data.GetProperty("message").GetProperty("model").GetString());

        // 用量只在收尾的 message_delta 里，且是互不重叠的形状。
        JsonElement usage = events[4].Data.GetProperty("usage");
        Assert.Equal(8, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.Equal(5, usage.GetProperty("output_tokens").GetInt64());

        // 上游的 OpenAI 帧一个都不能漏给客户端。
        Assert.DoesNotContain("[DONE]", recorder.WrittenText, StringComparison.Ordinal);
        Assert.DoesNotContain("choices", recorder.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorBeforeCommitWritesNothingAndIsRetryable()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(PreambleFrame);
        upstream.Write(ErrorFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: true), committer, CancellationToken.None);

        Assert.Equal(FailureClass.UpstreamOverloaded, outcome.FailureClass);
        Assert.False(outcome.Committed);
        Assert.False(recorder.HasStarted);
        Assert.False(outcome.ClientNotified);
    }

    [Fact]
    public async Task ErrorAfterCommitBecomesAnAnthropicErrorEvent()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Write(ErrorFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: true), committer, CancellationToken.None);

        Assert.Equal(FailureClass.UpstreamOverloaded, outcome.FailureClass);
        Assert.True(outcome.Committed);
        Assert.True(outcome.ClientNotified);

        var events = Events(recorder.WrittenText);
        Assert.Equal("error", events[^1].Name);
        Assert.Equal("api_error", events[^1].Data.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain(events, e => e.Name == "message_stop");

        // 上游的错误原文不透给客户端。
        Assert.DoesNotContain("overloaded", recorder.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCutAfterCommitEndsWithAnErrorEventNotMessageStop()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: true), committer, CancellationToken.None);

        Assert.Equal(FailureClass.StreamInterrupted, outcome.FailureClass);
        Assert.True(outcome.ClientNotified);
        var names = Events(recorder.WrittenText).Select(e => e.Name).ToList();
        Assert.Equal("error", names[^1]);
        Assert.DoesNotContain("message_stop", names);
    }

    [Fact]
    public async Task EmptyStreamBuffersACompleteEmptyMessageForHandover()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(PreambleFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: true), committer, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.EndedEmpty);
        Assert.False(recorder.HasStarted);

        // 没有候选了才交出去：客户端拿到的是完整、合法的一条空消息。
        await committer.FlushUncommittedAsync(CancellationToken.None);
        Assert.Equal(["message_start", "message_delta", "message_stop"], Events(recorder.WrittenText).Select(e => e.Name));
    }

    [Fact]
    public async Task NonStreamIsConvertedToAnAnthropicMessage()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.OK, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(
            "{\"model\":\"gpt-4o-2024-11-20\",\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: false), committer, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(10, outcome.Usage.InputTokens);
        Assert.Equal(
            """{"id":"msg_test","type":"message","role":"assistant","model":"gpt-4o-2024-11-20","content":[{"type":"text","text":"你好"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":3}}""",
            recorder.WrittenText);
    }

    [Fact]
    public async Task NonStreamWithInvalidToolArgumentsIsARetryableSoftError()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.OK, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write("{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"c\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"a\\\":\"}}]},\"finish_reason\":\"tool_calls\"}]}");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(Request(stream: false), committer, CancellationToken.None);

        Assert.Equal(FailureClass.SoftError, outcome.FailureClass);
        Assert.False(recorder.HasStarted);
    }

    [Fact]
    public async Task DifferentFormatsWithoutAConversionAreRefused()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        // 三种格式两两之间都能转，但格式不同又没给转换信息时，转发器要当场拒绝而不是把上游的字节原样交出去。
        ForwardRequest request = Request(stream: true);
        var unsupported = new ForwardRequest
        {
            RequestUri = request.RequestUri,
            Secret = request.Secret,
            UpstreamApiFormat = ApiFormat.OpenAiResponses,
            InboundApiFormat = ApiFormat.OpenAiChat,
            Body = request.Body,
            IsStream = true,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => forwarder.ForwardAsync(unsupported, committer, CancellationToken.None));
    }
}
