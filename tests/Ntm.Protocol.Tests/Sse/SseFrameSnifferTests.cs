using System.Text;
using Ntm.Protocol.Sse;

namespace Ntm.Protocol.Tests.Sse;

public sealed class SseFrameSnifferTests
{
    private static List<(string Event, string Data)> ReadAll(params string[] chunks)
    {
        using var sniffer = new SseFrameSniffer();
        var frames = new List<(string, string)>();

        foreach (string chunk in chunks)
        {
            sniffer.Append(Encoding.UTF8.GetBytes(chunk));
            while (sniffer.TryReadFrame())
            {
                frames.Add((Encoding.UTF8.GetString(sniffer.EventName), Encoding.UTF8.GetString(sniffer.Data)));
            }
        }

        if (sniffer.TryReadFinalFrame())
        {
            frames.Add((Encoding.UTF8.GetString(sniffer.EventName), Encoding.UTF8.GetString(sniffer.Data)));
        }

        return frames;
    }

    [Fact]
    public void SplitsSingleFrame()
    {
        var frames = ReadAll("data: {\"a\":1}\n\n");

        Assert.Single(frames);
        Assert.Equal("", frames[0].Event);
        Assert.Equal("{\"a\":1}", frames[0].Data);
    }

    [Fact]
    public void ReadsEventName()
    {
        var frames = ReadAll("event: message_start\ndata: {\"type\":\"message_start\"}\n\n");

        Assert.Single(frames);
        Assert.Equal("message_start", frames[0].Event);
        Assert.Equal("{\"type\":\"message_start\"}", frames[0].Data);
    }

    [Fact]
    public void SplitsMultipleFramesInOneChunk()
    {
        var frames = ReadAll("data: one\n\ndata: two\n\ndata: [DONE]\n\n");

        Assert.Equal(3, frames.Count);
        Assert.Equal("one", frames[0].Data);
        Assert.Equal("two", frames[1].Data);
        Assert.Equal("[DONE]", frames[2].Data);
    }

    [Fact]
    public void JoinsMultiLineDataWithNewline()
    {
        var frames = ReadAll("data: line one\ndata: line two\n\n");

        Assert.Single(frames);
        Assert.Equal("line one\nline two", frames[0].Data);
    }

    [Fact]
    public void SkipsCommentOnlyFrames()
    {
        // 心跳就是这个形状。它不该被当成一帧交出去。
        var frames = ReadAll(": keepalive\n\ndata: real\n\n");

        Assert.Single(frames);
        Assert.Equal("real", frames[0].Data);
    }

    [Fact]
    public void HandlesCommentInsideFrame()
    {
        var frames = ReadAll(": ping\ndata: real\n\n");

        Assert.Single(frames);
        Assert.Equal("real", frames[0].Data);
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("\r\r")]
    public void HandlesEveryLineEnding(string boundary)
    {
        string lineEnd = boundary[..(boundary.Length / 2)];
        var frames = ReadAll($"event: e{lineEnd}data: payload{boundary}");

        Assert.Single(frames);
        Assert.Equal("e", frames[0].Event);
        Assert.Equal("payload", frames[0].Data);
    }

    [Fact]
    public void ReassemblesFrameSplitAcrossChunks()
    {
        var frames = ReadAll("data: {\"cho", "ices\":[]}", "\n\n");

        Assert.Single(frames);
        Assert.Equal("{\"choices\":[]}", frames[0].Data);
    }

    [Fact]
    public void ReassemblesBoundarySplitAcrossChunks()
    {
        // 最难的情况：\r\n\r\n 被切在中间。
        var frames = ReadAll("data: a\r", "\n\r", "\ndata: b\r\n\r\n");

        Assert.Equal(2, frames.Count);
        Assert.Equal("a", frames[0].Data);
        Assert.Equal("b", frames[1].Data);
    }

    [Fact]
    public void SplitByteByByteYieldsSameFrames()
    {
        const string stream = "event: one\ndata: {\"i\":1}\n\n: hb\n\nevent: two\ndata: {\"i\":2}\n\ndata: [DONE]\n\n";
        string[] oneByteChunks = [.. stream.Select(c => c.ToString())];

        var frames = ReadAll(oneByteChunks);

        Assert.Equal(3, frames.Count);
        Assert.Equal(("one", "{\"i\":1}"), frames[0]);
        Assert.Equal(("two", "{\"i\":2}"), frames[1]);
        Assert.Equal(("", "[DONE]"), frames[2]);
    }

    [Fact]
    public void FinalFrameWithoutTrailingBlankLineIsStillRead()
    {
        // 上游直接断开，最后一帧没有收尾空行。
        var frames = ReadAll("data: tail");

        Assert.Single(frames);
        Assert.Equal("tail", frames[0].Data);
    }

    [Fact]
    public void FieldWithoutSpaceAfterColonIsRead()
    {
        var frames = ReadAll("data:{\"tight\":true}\n\n");

        Assert.Single(frames);
        Assert.Equal("{\"tight\":true}", frames[0].Data);
    }

    [Fact]
    public void IgnoresIdAndRetryFields()
    {
        var frames = ReadAll("id: 42\nretry: 3000\ndata: payload\n\n");

        Assert.Single(frames);
        Assert.Equal("payload", frames[0].Data);
    }

    [Fact]
    public void AbandonsWhenOneFrameExceedsTheCap()
    {
        using var sniffer = new SseFrameSniffer(maxFrameBytes: 2048);

        // 一直喂没有边界的字节。
        for (int i = 0; i < 10; i++)
        {
            sniffer.Append(Encoding.UTF8.GetBytes(new string('x', 512)));
        }

        Assert.True(sniffer.Abandoned);
        Assert.False(sniffer.TryReadFrame());
    }

    [Fact]
    public void KeepsWorkingAcrossManyFramesWithoutGrowingUnbounded()
    {
        // 帧数远超缓冲上限，但每帧都小：不该放弃。
        using var sniffer = new SseFrameSniffer(maxFrameBytes: 4096);
        int count = 0;

        for (int i = 0; i < 500; i++)
        {
            sniffer.Append(Encoding.UTF8.GetBytes($"data: {{\"i\":{i}}}\n\n"));
            while (sniffer.TryReadFrame())
            {
                count++;
            }
        }

        Assert.False(sniffer.Abandoned);
        Assert.Equal(500, count);
    }
}
