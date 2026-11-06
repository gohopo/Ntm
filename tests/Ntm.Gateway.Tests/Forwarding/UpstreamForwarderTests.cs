using System.Net;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Forwarding;

/// <summary>
/// 同格式透传的转发行为（ADR-0002、ADR-0008）：字节原样转发，用量从旁路嗅探，
/// 提交点之前的失败不写客户端。
/// </summary>
public sealed class UpstreamForwarderTests
{
    private const string PreambleFrame = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";
    private const string ContentFrame = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
    private const string FinishFrame = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string UsageFrame =
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n";
    private const string DoneFrame = "data: [DONE]\n\n";

    private static ForwardRequest StreamRequest(string body = "{\"model\":\"gpt-4o\",\"stream\":true}") => new()
    {
        RequestUri = new Uri("https://api.openai.com/v1/chat/completions"),
        Secret = "sk-test",
        UpstreamApiFormat = ApiFormat.OpenAiChat,
        InboundApiFormat = ApiFormat.OpenAiChat,
        Body = System.Text.Encoding.UTF8.GetBytes(body),
        IsStream = true,
    };

    [Fact]
    public async Task StreamIsForwardedByteForByteAndUsageComesFromTheSniffer()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(PreambleFrame);
        upstream.Write(ContentFrame);
        upstream.Write(FinishFrame);
        upstream.Write(UsageFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Committed);
        Assert.Equal("stop", outcome.FinishReason);

        // 客户端拿到的就是上游给的字节，一个不多一个不少。
        Assert.Equal(PreambleFrame + ContentFrame + FinishFrame + UsageFrame + DoneFrame, recorder.WrittenText);

        // 用量从旁路嗅探出来，五项互不重叠：prompt 12 里有 4 是缓存读。
        Assert.Equal(UsageSource.Upstream, outcome.Usage.Source);
        Assert.Equal(8, outcome.Usage.InputTokens);
        Assert.Equal(4, outcome.Usage.CacheReadTokens);
        Assert.Equal(5, outcome.Usage.OutputTokens);
    }

    [Fact]
    public async Task RequestBodyAndAuthHeaderGoUpstreamUnchanged()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        const string body = "{\"model\":\"gpt-4o\",\"stream\":true,\"temperature\":0.7}";
        await forwarder.ForwardAsync(StreamRequest(body), committer, CancellationToken.None);

        Assert.Equal(body, upstream.ReceivedBody);
        Assert.Equal("Bearer sk-test", upstream.ReceivedRequest!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task AnthropicGetsItsOwnAuthAndVersionHeaders()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Complete();

        var request = new ForwardRequest
        {
            RequestUri = new Uri("https://api.anthropic.com/v1/messages"),
            Secret = "sk-ant-test",
            UpstreamApiFormat = ApiFormat.Anthropic,
            InboundApiFormat = ApiFormat.Anthropic,
            Body = "{}"u8.ToArray(),
            IsStream = true,
        };

        await forwarder.ForwardAsync(request, committer, CancellationToken.None);

        Assert.Equal("sk-ant-test", upstream.ReceivedRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal(
            ApiFormatAuth.AnthropicDefaultVersion,
            upstream.ReceivedRequest.Headers.GetValues(ApiFormatAuth.AnthropicVersionHeader).Single());
        Assert.Null(upstream.ReceivedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task UpstreamErrorStatusNeverTouchesTheClient()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.TooManyRequests, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write("{\"error\":{\"message\":\"rate limit\",\"type\":\"rate_limit_error\"}}");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        Assert.Equal(FailureClass.RateLimited, outcome.FailureClass);
        Assert.Equal(429, outcome.HttpStatusCode);
        Assert.False(outcome.Committed);

        // 客户端什么都没收到，所以还能换候选。
        Assert.False(recorder.HasStarted);
        Assert.Empty(recorder.Written);
        Assert.Contains("rate limit", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FailureClass.AuthFailure)]
    [InlineData(HttpStatusCode.Forbidden, FailureClass.AuthFailure)]
    [InlineData(HttpStatusCode.PaymentRequired, FailureClass.QuotaExhausted)]
    [InlineData(HttpStatusCode.TooManyRequests, FailureClass.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.UnprocessableContent, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureClass.UpstreamOverloaded)]
    [InlineData(HttpStatusCode.InternalServerError, FailureClass.UpstreamServerError)]
    [InlineData(HttpStatusCode.BadGateway, FailureClass.UpstreamServerError)]
    public async Task StatusCodesMapToFailureClasses(HttpStatusCode status, FailureClass expected)
    {
        using var upstream = new FakeUpstream(status, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write("{\"error\":{\"message\":\"nope\"}}");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        Assert.Equal(expected, outcome.FailureClass);
    }

    [Fact]
    public async Task ModelNotFoundIsToldApartFromAPlain404()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.NotFound, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write("{\"error\":{\"message\":\"The model `gpt-5` does not exist\"}}");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        // 模型级问题冷却「渠道 + 模型」；路径配错是渠道级（Q28）。
        Assert.Equal(FailureClass.ModelNotFound, outcome.FailureClass);
    }

    [Fact]
    public async Task ConnectionFailureIsClassifiedAsSuch()
    {
        using var upstream = new FakeUpstream
        {
            ThrowOnSend = new HttpRequestException("Name or service not known"),
        };
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        Assert.Equal(FailureClass.ConnectionFailure, outcome.FailureClass);
        Assert.False(recorder.HasStarted);
    }

    [Fact]
    public async Task InStreamErrorBeforeCommitIsARetryableFailure()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        // 上游先发了序幕，然后在流里报错。HTTP 状态是 200。
        upstream.Write(PreambleFrame);
        upstream.Write("data: {\"error\":{\"message\":\"overloaded\",\"type\":\"overloaded_error\"}}\n\n");
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        // 流内错误按原文细分（Q28）：overloaded_error 是上游过载，不是笼统的软错误。
        Assert.Equal(FailureClass.UpstreamOverloaded, outcome.FailureClass);
        Assert.False(outcome.Committed);

        // 序幕被扣住了，客户端一个字节都没拿到，所以这次失败还能换候选。
        Assert.Empty(recorder.Written);
        Assert.Contains("overloaded", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanStreamWithoutAnyContentIsAFailedAttempt()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(PreambleFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        Assert.True(outcome.EndedEmpty);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Committed);

        // 失败的尝试不写客户端：字节留在序幕缓冲里，换候选时丢掉，没有候选时才由调用方交出去。
        Assert.Empty(recorder.Written);
        Assert.True(committer.BufferedBytes > 0);

        await committer.FlushUncommittedAsync(CancellationToken.None);
        Assert.Equal(PreambleFrame + DoneFrame, recorder.WrittenText);
    }

    [Fact]
    public async Task NonStreamResponseIsAtomicAndSoftErrorsAreCaught()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.OK, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        // HTTP 200 但响应体是错误，而且长度超过 512 字节——ccLoad 的探针做法会当成成功。
        string message = new('x', 2000);
        upstream.Write("{\"error\":{\"message\":\"" + message + "\",\"type\":\"server_error\"}}");
        upstream.Complete();

        var request = new ForwardRequest
        {
            RequestUri = new Uri("https://api.openai.com/v1/chat/completions"),
            Secret = "sk-test",
            UpstreamApiFormat = ApiFormat.OpenAiChat,
            InboundApiFormat = ApiFormat.OpenAiChat,
            Body = "{\"model\":\"gpt-4o\"}"u8.ToArray(),
            IsStream = false,
        };

        ForwardOutcome outcome = await forwarder.ForwardAsync(request, committer, CancellationToken.None);

        Assert.Equal(FailureClass.SoftError, outcome.FailureClass);
        Assert.False(outcome.Committed);
        Assert.Empty(recorder.Written);
    }

    [Fact]
    public async Task NonStreamSuccessIsWrittenWholeWithUsage()
    {
        using var upstream = new FakeUpstream(HttpStatusCode.OK, "application/json");
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        const string body =
            "{\"model\":\"gpt-4o-2024-11-20\",\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}";
        upstream.Write(body);
        upstream.Complete();

        var request = new ForwardRequest
        {
            RequestUri = new Uri("https://api.openai.com/v1/chat/completions"),
            Secret = "sk-test",
            UpstreamApiFormat = ApiFormat.OpenAiChat,
            InboundApiFormat = ApiFormat.OpenAiChat,
            Body = "{\"model\":\"gpt-4o\"}"u8.ToArray(),
            IsStream = false,
        };

        ForwardOutcome outcome = await forwarder.ForwardAsync(request, committer, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Committed);
        Assert.Equal(body, recorder.WrittenText);
        Assert.Equal(10, outcome.Usage.InputTokens);
        Assert.Equal(3, outcome.Usage.OutputTokens);

        // 响应里的模型名是上游实际用的那个，不改回别名（ADR-0002）。
        Assert.Equal("gpt-4o-2024-11-20", outcome.UpstreamModel);
    }

    [Fact]
    public async Task ClientDisconnectCancelsUpstreamWithoutCooldown()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);
        using var clientGone = new CancellationTokenSource();

        upstream.Write(PreambleFrame);

        Task<ForwardOutcome> forwarding = forwarder.ForwardAsync(
            StreamRequest(), committer, clientGone.Token);

        await upstream.RequestReceived;
        await clientGone.CancelAsync();

        ForwardOutcome outcome = await forwarding;

        // 客户端自己走的，不算渠道的错（Q20）。
        Assert.Equal(FailureClass.ClientDisconnected, outcome.FailureClass);
    }

    [Fact]
    public async Task ProtocolConversionIsNotImplementedYet()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        var request = new ForwardRequest
        {
            RequestUri = new Uri("https://api.openai.com/v1/chat/completions"),
            Secret = "sk-test",
            UpstreamApiFormat = ApiFormat.OpenAiChat,
            InboundApiFormat = ApiFormat.Anthropic,
            Body = "{}"u8.ToArray(),
            IsStream = true,
        };

        // 切片 2 的事。现在显式拒绝，而不是默默当成透传。
        await Assert.ThrowsAsync<NotSupportedException>(
            () => forwarder.ForwardAsync(request, committer, CancellationToken.None));
    }

    [Fact]
    public async Task InjectedUsageFrameIsSwallowedButStillCounted()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(PreambleFrame);
        upstream.Write(ContentFrame);
        upstream.Write(FinishFrame);
        upstream.Write(UsageFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        ForwardRequest request = StreamRequest();
        ForwardOutcome outcome = await forwarder.ForwardAsync(
            new ForwardRequest
            {
                RequestUri = request.RequestUri,
                Secret = request.Secret,
                UpstreamApiFormat = request.UpstreamApiFormat,
                InboundApiFormat = request.InboundApiFormat,
                Body = request.Body,
                IsStream = true,
                SwallowUsageOnlyFrames = true,
            },
            committer,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);

        // 客户端没要 usage：那一帧被剔掉，其余字节一个不差，[DONE] 照常到达。
        Assert.Equal(PreambleFrame + ContentFrame + FinishFrame + DoneFrame, recorder.WrittenText);
        Assert.DoesNotContain("usage", recorder.WrittenText, StringComparison.Ordinal);

        // 但账还是记下来了。
        Assert.Equal(8, outcome.Usage.InputTokens);
        Assert.Equal(4, outcome.Usage.CacheReadTokens);
        Assert.Equal(5, outcome.Usage.OutputTokens);
    }

    [Fact]
    public async Task SwallowingWorksWhenFramesArriveSplitAcrossChunks()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        string wire = PreambleFrame + ContentFrame + FinishFrame + UsageFrame + DoneFrame;
        foreach (char c in wire)
        {
            upstream.Write(c.ToString());
        }

        upstream.Complete();

        ForwardRequest template = StreamRequest();
        ForwardOutcome outcome = await forwarder.ForwardAsync(
            new ForwardRequest
            {
                RequestUri = template.RequestUri,
                Secret = template.Secret,
                UpstreamApiFormat = template.UpstreamApiFormat,
                InboundApiFormat = template.InboundApiFormat,
                Body = template.Body,
                IsStream = true,
                SwallowUsageOnlyFrames = true,
            },
            committer,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(PreambleFrame + ContentFrame + FinishFrame + DoneFrame, recorder.WrittenText);
        Assert.Equal(5, outcome.Usage.OutputTokens);
    }

    [Fact]
    public async Task ClientRequestedUsageFrameIsNotSwallowed()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Write(UsageFrame);
        upstream.Write(DoneFrame);
        upstream.Complete();

        // 客户端自己写了 include_usage，那一帧是它要的，必须原样送到。
        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest("{\"model\":\"gpt-4o\",\"stream\":true,\"stream_options\":{\"include_usage\":true}}"),
            committer,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(ContentFrame + UsageFrame + DoneFrame, recorder.WrittenText);
    }

    [Fact]
    public async Task StreamCutShortAfterCommitIsAFailureThatCannotRetry()
    {
        using var upstream = new FakeUpstream();
        using var invoker = new HttpMessageInvoker(upstream);
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);
        var forwarder = new UpstreamForwarder(invoker);

        upstream.Write(ContentFrame);
        upstream.Complete();

        ForwardOutcome outcome = await forwarder.ForwardAsync(
            StreamRequest(), committer, CancellationToken.None);

        // 提交之后断流：不能换候选，但也不是成功。这是模型级的（StreamInterrupted），
        // 不是传输层的 ConnectionFailure，所以只冷却「渠道 + 模型」。
        Assert.True(outcome.Committed);
        Assert.Equal(FailureClass.StreamInterrupted, outcome.FailureClass);
        Assert.Equal(ContentFrame, recorder.WrittenText);
    }
}
