using System.Net;
using System.Text;
using Ntm.Gateway.Debugging;

namespace Ntm.Gateway.Tests.Debugging;

/// <summary>一次 Attempt 的三份原文（Q156/Q157）：截断按字节算，流式分块不会把多字节字符切坏。</summary>
public class AttemptDebugCaptureTests
{
    private static readonly Guid RequestId = Guid.CreateVersion7();

    private static AttemptDebugCapture NewCapture() => new(RequestId, 1, DateTimeOffset.UnixEpoch)
    {
        Secrets = ["sk-upstream-secret"],
    };

    [Fact]
    public void AStreamSplitInsideAMultiByteCharacterIsStoredIntact()
    {
        AttemptDebugCapture capture = NewCapture();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        capture.CaptureResponseHead(200, response.Headers, null);

        byte[] bytes = Encoding.UTF8.GetBytes("data: 你好\n\n");
        capture.CaptureResponseChunk(bytes.AsSpan(0, 7));   // 「你」的第一个字节
        capture.CaptureResponseChunk(bytes.AsSpan(7));

        DebugPayload payload = Assert.Single(capture.Finish());
        Assert.Equal(DebugPayloadKind.UpstreamResponse, payload.Kind);
        Assert.Equal("data: 你好\n\n", payload.Body);
        Assert.Equal(bytes.Length, payload.ByteCount);
        Assert.False(payload.Truncated);
    }

    [Fact]
    public void AResponseOverTheLimitIsTruncatedButStillCountsEveryByte()
    {
        AttemptDebugCapture capture = NewCapture();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        capture.CaptureResponseHead(200, response.Headers, null);

        byte[] chunk = new byte[600 * 1024];
        chunk.AsSpan().Fill((byte)'a');
        capture.CaptureResponseChunk(chunk);
        capture.CaptureResponseChunk(chunk);
        capture.CaptureResponseChunk(chunk);

        DebugPayload payload = Assert.Single(capture.Finish());
        Assert.True(payload.Truncated);
        Assert.Equal(DebugPayload.MaxBytes, payload.Body.Length);
        Assert.Equal(3 * chunk.Length, payload.ByteCount);
    }

    [Fact]
    public void ARequestBodyOverTheLimitIsTruncated()
    {
        AttemptDebugCapture capture = NewCapture();
        byte[] body = new byte[DebugPayload.MaxBytes + 10];
        body.AsSpan().Fill((byte)'x');

        capture.CaptureClientRequest([], body);

        DebugPayload payload = Assert.Single(capture.Finish());
        Assert.Equal(DebugPayloadKind.ClientRequest, payload.Kind);
        Assert.True(payload.Truncated);
        Assert.Equal(DebugPayload.MaxBytes, payload.Body.Length);
        Assert.Equal(body.Length, payload.ByteCount);
    }

    [Fact]
    public void TheUpstreamRequestIsRedactedButItsBodyIsNot()
    {
        AttemptDebugCapture capture = NewCapture();
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://upstream.test/v1/chat/completions");
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-upstream-secret");
        message.Headers.TryAddWithoutValidation("x-relay-token", "sk-upstream-secret");
        byte[] body = Encoding.UTF8.GetBytes("{\"note\":\"sk-upstream-secret\"}");

        capture.CaptureUpstreamRequest(message, body);

        DebugPayload payload = Assert.Single(capture.Finish());
        Assert.Contains("Authorization: Bearer sk-ups***", payload.Headers, StringComparison.Ordinal);
        Assert.Contains("x-relay-token: sk-ups***", payload.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-upstream-secret", payload.Headers, StringComparison.Ordinal);
        Assert.Equal("{\"note\":\"sk-upstream-secret\"}", payload.Body);
    }

    [Fact]
    public void WithoutAResponseThereIsNoResponsePayload()
    {
        AttemptDebugCapture capture = NewCapture();
        capture.CaptureClientRequest([], "{}"u8);

        DebugPayload payload = Assert.Single(capture.Finish());
        Assert.Equal(DebugPayloadKind.ClientRequest, payload.Kind);
    }
}
