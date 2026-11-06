using System.Text;
using Ntm.Protocol;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

public sealed class OpenAiChatStreamInspectorTests
{
    private static StreamFrameVerdict Inspect(OpenAiChatStreamInspector inspector, string json) =>
        inspector.Inspect(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void RoleOnlyFirstChunkIsPreamble()
    {
        var inspector = new OpenAiChatStreamInspector();

        var verdict = Inspect(inspector, """
            {"id":"1","object":"chat.completion.chunk","model":"gpt-4o-mini","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}
            """);

        Assert.Equal(StreamFrameVerdict.Preamble, verdict);
        Assert.False(inspector.HasContentEvent);
        Assert.Equal("gpt-4o-mini", inspector.UpstreamModel);
    }

    [Fact]
    public void EmptyDeltaIsPreamble()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, """
            {"choices":[{"index":0,"delta":{},"finish_reason":null}]}
            """));
    }

    [Fact]
    public void ContentDeltaIsContentEvent()
    {
        var inspector = new OpenAiChatStreamInspector();

        var verdict = Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}
            """);

        Assert.Equal(StreamFrameVerdict.Content, verdict);
        Assert.True(inspector.HasContentEvent);
    }

    [Fact]
    public void ReasoningContentIsContentEvent()
    {
        // 推理也是真实输出（GLOSSARY: Reasoning 是一种 ContentBlock）。DeepSeek 用 reasoning_content。
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"reasoning_content":"let me think"},"finish_reason":null}]}
            """));        Assert.True(inspector.SawThinking);
    }

    [Fact]
    public void ReasoningFieldVariantIsContentEvent()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"reasoning":"hmm"},"finish_reason":null}]}
            """));        Assert.True(inspector.SawThinking);
    }

    [Fact]
    public void AnswerTextOrAnEmptyReasoningFieldIsNotThinking()
    {
        // 计价按「输出了思考过程」判定（ADR-0013）：空字符串和 null 不算。
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"content":"hi","reasoning_content":""},"finish_reason":null}]}
            """);
        Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"reasoning_content":null},"finish_reason":"stop"}]}
            """);

        Assert.False(inspector.SawThinking);
    }

    [Fact]
    public void ToolCallDeltaIsContentEvent()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"f","arguments":""}}]},"finish_reason":null}]}
            """));
    }

    [Fact]
    public void EmptyToolCallArrayIsPreamble()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, """
            {"choices":[{"index":0,"delta":{"tool_calls":[]},"finish_reason":null}]}
            """));
    }

    [Fact]
    public void FinishReasonAloneIsContentEvent()
    {
        // 首帧就 content_filter：模型已经给出结论，重试只会再被同一个过滤器拦住。
        var inspector = new OpenAiChatStreamInspector();

        var verdict = Inspect(inspector, """
            {"choices":[{"index":0,"delta":{},"finish_reason":"content_filter"}]}
            """);

        Assert.Equal(StreamFrameVerdict.Content, verdict);
        Assert.Equal("content_filter", inspector.FinishReason);
    }

    [Fact]
    public void UsageOnlyChunkIsNotContentEvent()
    {
        var inspector = new OpenAiChatStreamInspector();

        var verdict = Inspect(inspector, """
            {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}
            """);

        Assert.Equal(StreamFrameVerdict.UsageOnly, verdict);
        Assert.False(inspector.HasContentEvent);
        Assert.Equal(UsageSource.Upstream, inspector.Usage.Source);
        Assert.Equal(100, inspector.Usage.InputTokens);
        Assert.Equal(20, inspector.Usage.OutputTokens);
    }

    [Fact]
    public void CachedTokensAreSubtractedFromInput()
    {
        // OpenAI 的 prompt_tokens 含缓存读。五项必须互不重叠（ADR-0005）。
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """
            {"choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":50,"prompt_tokens_details":{"cached_tokens":900}}}
            """);

        Assert.Equal(100, inspector.Usage.InputTokens);
        Assert.Equal(900, inspector.Usage.CacheReadTokens);
        Assert.Equal(50, inspector.Usage.OutputTokens);
        Assert.Equal(1000, inspector.Usage.TotalInputTokens);
    }

    [Fact]
    public void DeepSeekCacheHitFieldIsRead()
    {
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """
            {"choices":[],"usage":{"prompt_tokens":500,"completion_tokens":10,"prompt_cache_hit_tokens":400,"prompt_cache_miss_tokens":100}}
            """);

        Assert.Equal(100, inspector.Usage.InputTokens);
        Assert.Equal(400, inspector.Usage.CacheReadTokens);
    }

    [Fact]
    public void InconsistentUpstreamNumbersAreClampedNotNegative()
    {
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """
            {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":999}}}
            """);

        Assert.Equal(0, inspector.Usage.InputTokens);
        Assert.Equal(999, inspector.Usage.CacheReadTokens);
    }

    [Fact]
    public void DoneMarkerIsTerminal()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Terminal, inspector.Inspect("[DONE]"u8));
        Assert.True(inspector.SawTerminal);
    }

    [Fact]
    public void ErrorObjectIsError()
    {
        // HTTP 200 但响应体是错误，按失败处理（规范「软错误检测」）。
        var inspector = new OpenAiChatStreamInspector();

        var verdict = Inspect(inspector, """
            {"error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit_exceeded"}}
            """);

        Assert.Equal(StreamFrameVerdict.Error, verdict);
        Assert.Contains("rate_limit_exceeded", inspector.ErrorPayload);
    }

    [Theory]
    [InlineData("""{"error":null,"choices":[{"delta":{"content":"ok"}}]}""")]
    [InlineData("""{"error":{},"choices":[{"delta":{"content":"ok"}}]}""")]
    [InlineData("""{"error":"","choices":[{"delta":{"content":"ok"}}]}""")]
    public void EmptyErrorFieldIsNoise(string json)
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, json));
    }

    [Fact]
    public void MalformedJsonIsReportedWithoutThrowing()
    {
        var inspector = new OpenAiChatStreamInspector();

        Assert.Equal(StreamFrameVerdict.Malformed, Inspect(inspector, "{not json"));
        Assert.False(inspector.HasContentEvent);
    }

    [Fact]
    public void CleanStreamWithNoOutputIsFlaggedEmpty()
    {
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """{"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""");
        inspector.Inspect("[DONE]"u8);

        Assert.True(inspector.EndedEmpty);
    }

    [Fact]
    public void StreamWithOutputTokensIsNotFlaggedEmpty()
    {
        // 判定可能漏了，但上游说产出过 token。别重试，否则双重计费。
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7}}""");
        inspector.Inspect("[DONE]"u8);

        Assert.False(inspector.HasContentEvent);
        Assert.False(inspector.EndedEmpty);
    }

    [Fact]
    public void NormalStreamIsNotFlaggedEmpty()
    {
        var inspector = new OpenAiChatStreamInspector();

        Inspect(inspector, """{"choices":[{"delta":{"content":"hi"}}]}""");
        inspector.Inspect("[DONE]"u8);

        Assert.True(inspector.HasContentEvent);
        Assert.False(inspector.EndedEmpty);
    }
}
