using System.Net;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Forwarding;

namespace Ntm.Gateway.Tests.Forwarding;

/// <summary>提交点的行为（ADR-0008）：发响应头不是提交，序幕按原顺序补发，提交后不能换候选。</summary>
public sealed class ResponseCommitterTests
{
    private static readonly byte[] RoleOnlyFrame =
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n"u8.ToArray();

    private static readonly byte[] ContentFrame =
        "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n"u8.ToArray();

    [Fact]
    public void BufferedPreambleReachesNobody()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        committer.BufferPreamble(RoleOnlyFrame);

        Assert.False(committer.Committed);
        Assert.False(recorder.HasStarted);
        Assert.Empty(recorder.Written);
        Assert.Equal(RoleOnlyFrame.Length, committer.BufferedBytes);
    }

    [Fact]
    public async Task CommitReplaysPreambleInOrderThenTheContentFrame()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        committer.BufferPreamble(RoleOnlyFrame);
        await committer.CommitAsync(ContentFrame, CancellationToken.None);

        Assert.True(committer.Committed);
        Assert.True(recorder.BufferingDisabled);
        Assert.Equal(0, committer.BufferedBytes);

        // 序幕在前，内容帧在后，顺序就是上游给的顺序。
        Assert.Equal([.. RoleOnlyFrame, .. ContentFrame], recorder.Written);
    }

    [Fact]
    public void DiscardingPreambleLeavesNothingForTheNextCandidate()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        committer.BufferPreamble(RoleOnlyFrame);
        committer.DiscardPreamble();

        Assert.Equal(0, committer.BufferedBytes);
        Assert.Empty(recorder.Written);
    }

    [Fact]
    public async Task CommittedResponseRefusesToSwitchCandidates()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        await committer.CommitAsync(ContentFrame, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(committer.DiscardPreamble);
    }

    [Fact]
    public void HeaderCapIsNotReachedBeforeTheDelay()
    {
        var time = new FakeTimeProvider();
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response, time);

        committer.MarkRequestStarted();
        time.Advance(TimeSpan.FromSeconds(9));

        Assert.False(committer.ShouldSendHeadersNow());
    }

    [Fact]
    public async Task AtTheCapHeadersGoOutButTheRequestIsNotCommitted()
    {
        var time = new FakeTimeProvider();
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response, time);

        committer.MarkRequestStarted();
        committer.BufferPreamble(RoleOnlyFrame);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(committer.ShouldSendHeadersNow());
        await committer.SendStreamHeadersEarlyAsync(CancellationToken.None);

        // 响应头出去了。
        Assert.True(recorder.HasStarted);
        Assert.Equal(200, recorder.StatusCodeWhenStarted);
        Assert.Equal("text/event-stream", recorder.HeadersWhenStarted["Content-Type"]);
        Assert.Equal("no-cache", recorder.HeadersWhenStarted["Cache-Control"]);

        // 前置 nginx 默认会缓冲 SSE。
        Assert.Equal("no", recorder.HeadersWhenStarted["X-Accel-Buffering"]);

        // 但这不是提交：重试窗口还开着，序幕也还攒着。
        Assert.False(committer.Committed);
        Assert.Equal(RoleOnlyFrame.Length, committer.BufferedBytes);

        // 而且客户端还没拿到任何事件。
        Assert.Empty(recorder.Written);
    }

    [Fact]
    public async Task KeepAliveIsACommentSoItDoesNotCommit()
    {
        var time = new FakeTimeProvider();
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response, time);

        committer.MarkRequestStarted();
        time.Advance(TimeSpan.FromSeconds(10));
        await committer.SendStreamHeadersEarlyAsync(CancellationToken.None);

        Assert.NotNull(committer.NextKeepAliveAt);
        time.Advance(ResponseCommitter.KeepAliveInterval);
        await committer.WriteKeepAliveAsync(CancellationToken.None);

        Assert.False(committer.Committed);
        Assert.Equal(": keepalive\n\n", recorder.WrittenText);

        // 下一次的时刻被重排了。
        Assert.Equal(time.GetUtcNow() + ResponseCommitter.KeepAliveInterval, committer.NextKeepAliveAt);
    }

    [Fact]
    public async Task AfterTheCapTheContentFrameStillReplaysThePreamble()
    {
        var time = new FakeTimeProvider();
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response, time);

        committer.MarkRequestStarted();
        committer.BufferPreamble(RoleOnlyFrame);
        time.Advance(TimeSpan.FromSeconds(10));
        await committer.SendStreamHeadersEarlyAsync(CancellationToken.None);
        time.Advance(ResponseCommitter.KeepAliveInterval);
        await committer.WriteKeepAliveAsync(CancellationToken.None);

        await committer.CommitAsync(ContentFrame, CancellationToken.None);

        // keepalive 注释、序幕、内容帧，按这个顺序。客户端会忽略注释行。
        Assert.Equal(": keepalive\n\ndata: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n", recorder.WrittenText);
        Assert.True(committer.Committed);
        Assert.Null(committer.NextKeepAliveAt);
    }

    [Fact]
    public async Task UpstreamHeadIsCopiedWithoutHopByHopHeaders()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        using var upstream = new HttpResponseMessage(HttpStatusCode.OK);
        upstream.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        upstream.Headers.TryAddWithoutValidation("Transfer-Encoding", "chunked");
        upstream.Headers.TryAddWithoutValidation("x-request-id", "req_123");
        upstream.Content = new ByteArrayContent([]);
        upstream.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

        committer.CopyUpstreamHead(upstream);
        await committer.CommitAsync(ContentFrame, CancellationToken.None);

        Assert.Equal("req_123", recorder.HeadersWhenStarted["x-request-id"]);
        Assert.Equal("text/event-stream", recorder.HeadersWhenStarted["Content-Type"]);

        // 逐跳头由本地连接决定，不照搬上游的。
        Assert.False(recorder.HeadersWhenStarted.ContainsKey("Connection"));
        Assert.False(recorder.HeadersWhenStarted.ContainsKey("Transfer-Encoding"));
    }

    [Fact]
    public async Task StatusCodeCannotBeChangedOnceHeadersWentOut()
    {
        var time = new FakeTimeProvider();
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response, time);

        committer.MarkRequestStarted();
        time.Advance(TimeSpan.FromSeconds(10));
        await committer.SendStreamHeadersEarlyAsync(CancellationToken.None);

        using var upstream = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        upstream.Content = new ByteArrayContent([]);
        committer.CopyUpstreamHead(upstream);

        // 已经按上限发过 200 了：状态码收不回来，失败只能走流内错误事件。这是 Q25 认下的代价。
        Assert.Equal(200, recorder.StatusCodeWhenStarted);
    }

    [Fact]
    public async Task FlushUncommittedHandsOverTheBytesWithoutClaimingCommit()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        committer.BufferPreamble(RoleOnlyFrame);
        await committer.FlushUncommittedAsync(CancellationToken.None);

        Assert.Equal(RoleOnlyFrame, recorder.Written);
        Assert.False(committer.Committed);
        Assert.Equal(0, committer.BufferedBytes);
    }

    [Fact]
    public async Task WritingCommittedBytesBeforeCommitIsAProgrammingError()
    {
        using var recorder = new RecordingHttpResponse();
        using var committer = new ResponseCommitter(recorder.Response);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => committer.WriteCommittedAsync(ContentFrame, CancellationToken.None));
    }
}
