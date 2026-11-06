using System.Text;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.Anthropic;

/// <summary>OpenAI Chat 非流式响应 → IR → Anthropic Messages 响应。</summary>
public sealed class AnthropicResponseWriterTests
{
    private const string MessageId = "msg_0123";

    private static string Convert(string openAiJson, string fallbackModel = "claude-x")
    {
        IrResponse? ir = OpenAiChatResponseConverter.ToIr(Encoding.UTF8.GetBytes(openAiJson));
        Assert.NotNull(ir);
        AnthropicResponseWriting writing = AnthropicResponseWriter.Write(ir, MessageId, fallbackModel);
        Assert.True(writing.Succeeded, writing.Error);
        return Encoding.UTF8.GetString(writing.Body!);
    }

    [Fact]
    public void WritesTextWithDisjointUsage()
    {
        string json = Convert("""
            {"id":"c","model":"gpt-4o-2024-11-20","choices":[{"index":0,"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":12,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4}}}
            """);

        // 模型名是上游真实名（ADR-0002）；input_tokens 不含缓存（ADR-0005）。
        Assert.Equal(
            """{"id":"msg_0123","type":"message","role":"assistant","model":"gpt-4o-2024-11-20","content":[{"type":"text","text":"你好"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":8,"cache_creation_input_tokens":0,"cache_read_input_tokens":4,"output_tokens":5}}""",
            json);
    }

    [Fact]
    public void ReasoningComesFirstWithEmptySignature()
    {
        string json = Convert("""
            {"choices":[{"message":{"role":"assistant","content":"答","reasoning_content":"想"},"finish_reason":"stop"}]}
            """);

        // 签名留空，不伪造；没有模型名时用客户端的。
        Assert.Equal(
            """{"id":"msg_0123","type":"message","role":"assistant","model":"claude-x","content":[{"type":"thinking","thinking":"想","signature":""},{"type":"text","text":"答"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0}}""",
            json);
    }

    [Fact]
    public void ToolCallsBecomeToolUseWithObjectInput()
    {
        string json = Convert("""
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"上海\"}"}},
              {"id":"call_2","type":"function","function":{"name":"time","arguments":""}}]},"finish_reason":"tool_calls"}]}
            """);

        Assert.Contains("""[{"type":"tool_use","id":"call_1","name":"weather","input":{"city":"上海"}},{"type":"tool_use","id":"call_2","name":"time","input":{}}]""", json, StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"tool_use\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallsReportedAsStopStillEndWithToolUse()
    {
        string json = Convert("""
            {"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_1","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"stop"}]}
            """);

        Assert.Contains("\"stop_reason\":\"tool_use\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidToolArgumentsFailInsteadOfPretending()
    {
        IrResponse? ir = OpenAiChatResponseConverter.ToIr(Encoding.UTF8.GetBytes("""
            {"choices":[{"message":{"tool_calls":[{"id":"c","function":{"name":"f","arguments":"{\"a\":"}}]},"finish_reason":"tool_calls"}]}
            """));

        AnthropicResponseWriting writing = AnthropicResponseWriter.Write(ir!, MessageId, "m");

        Assert.False(writing.Succeeded);
        Assert.Contains("工具 f", writing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayArgumentsAreNotAnObject()
    {
        IrResponse? ir = OpenAiChatResponseConverter.ToIr(Encoding.UTF8.GetBytes("""
            {"choices":[{"message":{"tool_calls":[{"id":"c","function":{"name":"f","arguments":"[1]"}}]}}]}
            """));

        Assert.False(AnthropicResponseWriter.Write(ir!, MessageId, "m").Succeeded);
    }

    [Theory]
    [InlineData("length", "max_tokens")]
    [InlineData("content_filter", "refusal")]
    [InlineData("stop", "end_turn")]
    [InlineData("something_new", "end_turn")]
    public void MapsFinishReasons(string finish, string expected)
    {
        string json = Convert("{\"choices\":[{\"message\":{\"content\":\"x\"},\"finish_reason\":\"" + finish + "\"}]}");

        Assert.Contains("\"stop_reason\":\"" + expected + "\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalBecomesTextWithRefusalStop()
    {
        string json = Convert("""{"choices":[{"message":{"content":null,"refusal":"不能"},"finish_reason":"stop"}]}""");

        Assert.Contains("""[{"type":"text","text":"不能"}]""", json, StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"refusal\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"object":"chat.completion"}""")]
    public void UnreadableBodiesYieldNull(string body) =>
        Assert.Null(OpenAiChatResponseConverter.ToIr(Encoding.UTF8.GetBytes(body)));
}
