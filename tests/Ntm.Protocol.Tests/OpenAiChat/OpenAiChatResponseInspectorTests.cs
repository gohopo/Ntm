using System.Text;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

public sealed class OpenAiChatResponseInspectorTests
{
    private static OpenAiChatResponseReading Read(string json) =>
        OpenAiChatResponseInspector.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ReadsNormalResponse()
    {
        var reading = Read("""
            {"id":"chatcmpl-1","object":"chat.completion","model":"gpt-4o-mini",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}
            """);

        Assert.False(reading.IsError);
        Assert.False(reading.IsMalformed);
        Assert.True(reading.HasOutput);
        Assert.Equal("stop", reading.FinishReason);
        Assert.Equal("gpt-4o-mini", reading.UpstreamModel);
        Assert.Equal(12, reading.Usage.InputTokens);
        Assert.Equal(3, reading.Usage.OutputTokens);
        Assert.Equal(UsageSource.Upstream, reading.Usage.Source);
    }

    [Fact]
    public void DetectsErrorBodyUnderHttp200()
    {
        var reading = Read("""
            {"error":{"message":"当前模型负载过高","type":"server_error","code":null}}
            """);

        Assert.True(reading.IsError);
        Assert.Contains("负载过高", reading.ErrorPayload);
    }

    [Fact]
    public void DetectsErrorBodyLargerThan512Bytes()
    {
        // 参考实现（ccLoad）只探前 512 字节且要求 json.Valid，于是这种体当成成功漏过去。
        string padding = new('x', 2000);
        var reading = Read("{\"error\":{\"message\":\"" + padding + "\",\"type\":\"server_error\"}}");

        Assert.True(reading.IsError);
    }

    [Fact]
    public void ToolCallResponseHasOutput()
    {
        var reading = Read("""
            {"choices":[{"index":0,"message":{"role":"assistant","content":null,
             "tool_calls":[{"id":"call_1","type":"function","function":{"name":"f","arguments":"{}"}}]},
             "finish_reason":"tool_calls"}]}
            """);

        Assert.True(reading.HasOutput);
        Assert.Equal("tool_calls", reading.FinishReason);
    }

    [Fact]
    public void ReasoningOnlyResponseHasOutput()
    {
        var reading = Read("""
            {"choices":[{"index":0,"message":{"role":"assistant","reasoning_content":"thinking"},"finish_reason":"stop"}]}
            """);

        Assert.True(reading.HasOutput);
        Assert.True(reading.SawThinking);
    }

    [Fact]
    public void AnAnswerWithoutReasoningIsNotThinking()
    {
        var reading = Read("""
            {"choices":[{"index":0,"message":{"role":"assistant","content":"hi","reasoning_content":""},"finish_reason":"stop"}]}
            """);

        Assert.True(reading.HasOutput);
        Assert.False(reading.SawThinking);
    }

    [Fact]
    public void EmptyChoicesHasNoOutput()
    {
        var reading = Read("""{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":0}}""");

        Assert.False(reading.HasOutput);
        Assert.False(reading.IsError);
    }

    [Fact]
    public void NullContentWithoutToolCallsHasNoOutput()
    {
        var reading = Read("""
            {"choices":[{"index":0,"message":{"role":"assistant","content":null},"finish_reason":"stop"}]}
            """);

        Assert.False(reading.HasOutput);
    }

    [Fact]
    public void CachedTokensAreSubtracted()
    {
        var reading = Read("""
            {"choices":[{"message":{"content":"x"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":800,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":768}}}
            """);

        Assert.Equal(32, reading.Usage.InputTokens);
        Assert.Equal(768, reading.Usage.CacheReadTokens);
    }

    [Fact]
    public void MalformedBodyIsReported()
    {
        var reading = Read("not json at all");

        Assert.True(reading.IsMalformed);
        Assert.False(reading.IsError);
    }

    [Fact]
    public void SseLookingBodyIsMalformedNotError()
    {
        // 非流式路径收到 SSE 文本：解析不了，但别据此判失败。
        var reading = Read("data: {\"choices\":[]}\n\n");

        Assert.True(reading.IsMalformed);
        Assert.False(reading.IsError);
    }

    [Fact]
    public void EmptyBodyIsMalformed()
    {
        Assert.True(OpenAiChatResponseInspector.Read([]).IsMalformed);
    }

    [Fact]
    public void JsonArrayBodyIsMalformed()
    {
        Assert.True(Read("[1,2,3]").IsMalformed);
    }

    [Fact]
    public void MissingUsageIsUnavailableNotZero()
    {
        var reading = Read("""{"choices":[{"message":{"content":"x"},"finish_reason":"stop"}]}""");

        Assert.False(reading.Usage.HasValue);
        Assert.Equal(UsageSource.Unavailable, reading.Usage.Source);
    }
}
