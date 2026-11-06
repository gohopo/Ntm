using System.Text;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

/// <summary>经由 IR 从 Anthropic 请求写出 OpenAI Chat 请求：断言整段输出，确定性写出才能这样比。</summary>
public sealed class OpenAiChatRequestWriterTests
{
    private static string Convert(string anthropicJson, string upstreamModel = "gpt-4o")
    {
        AnthropicRequestReading reading = AnthropicRequestReader.Read(Encoding.UTF8.GetBytes(anthropicJson));
        Assert.True(reading.IsValid, reading.Error);
        return Encoding.UTF8.GetString(OpenAiChatRequestWriter.Write(reading.Request!, upstreamModel));
    }

    [Fact]
    public void WritesAPlainConversationWithSystemAndParameters()
    {
        string json = Convert("""
            {"model":"claude-x","max_tokens":100,"temperature":0.5,"stop_sequences":["END"],
             "system":[{"type":"text","text":"a"},{"type":"text","text":"b"}],
             "metadata":{"user_id":"u-1"},
             "messages":[{"role":"user","content":"你好"},{"role":"assistant","content":"在"},{"role":"user","content":[{"type":"text","text":"x"},{"type":"text","text":"y"}]}]}
            """);

        // 模型名换成上游名；中文不转义；纯文字 content 写成字符串。
        Assert.Equal(
            """{"model":"gpt-4o","messages":[{"role":"system","content":"a\n\nb"},{"role":"user","content":"你好"},{"role":"assistant","content":"在"},{"role":"user","content":"xy"}],"max_tokens":100,"temperature":0.5,"stop":["END"],"user":"u-1"}""",
            json);
    }

    [Fact]
    public void StreamingAlwaysAsksForUsage()
    {
        string json = Convert("""{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        Assert.EndsWith("""
            "stream":true,"stream_options":{"include_usage":true}}
            """, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolRoundTripBecomesToolCallsAndToolMessages()
    {
        string json = Convert("""
            {"model":"m","messages":[
              {"role":"user","content":"天气？"},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"plan","signature":"sig"},
                {"type":"text","text":"查一下"},
                {"type":"tool_use","id":"toolu_1","name":"weather","input":{"city":"北京"}},
                {"type":"tool_use","id":"toolu_2","name":"weather","input":{"city":"上海"}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_1","content":"晴"},
                {"type":"tool_result","tool_use_id":"toolu_2","content":[{"type":"text","text":"雨"}],"is_error":true},
                {"type":"text","text":"总结"}]}]}
            """);

        // 推理块丢掉；工具参数是 JSON 字符串；tool 消息排在后续 user 文字之前。
        Assert.Equal(
            """{"model":"gpt-4o","messages":[{"role":"user","content":"天气？"},{"role":"assistant","content":"查一下","tool_calls":[{"id":"toolu_1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"北京\"}"}},{"id":"toolu_2","type":"function","function":{"name":"weather","arguments":"{\"city\":\"上海\"}"}}]},{"role":"tool","tool_call_id":"toolu_1","content":"晴"},{"role":"tool","tool_call_id":"toolu_2","content":"雨"},{"role":"user","content":"总结"}]}""",
            json);
    }

    [Fact]
    public void AssistantWithOnlyToolCallsHasNullContent()
    {
        string json = Convert("""
            {"model":"m","messages":[{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"now","input":{}}]}]}
            """);

        Assert.Contains("""{"role":"assistant","content":null,"tool_calls":""", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReasoningOnlyAssistantMessageIsDropped()
    {
        string json = Convert("""
            {"model":"m","messages":[{"role":"user","content":"a"},{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"s"}]},{"role":"user","content":"b"}]}
            """);

        Assert.Equal(
            """{"model":"gpt-4o","messages":[{"role":"user","content":"a"},{"role":"user","content":"b"}]}""",
            json);
    }

    [Fact]
    public void ImagesBecomeImageUrlPartsAndToolResultImagesMoveToAUserMessage()
    {
        string json = Convert("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"截图如下"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAA"}}]},
              {"type":"image","source":{"type":"url","url":"https://example.com/b.png"}},
              {"type":"text","text":"看看"}]}]}
            """);

        Assert.Equal(
            """{"model":"gpt-4o","messages":[{"role":"tool","tool_call_id":"t1","content":"截图如下"},{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AAA"}},{"type":"image_url","image_url":{"url":"https://example.com/b.png"}},{"type":"text","text":"看看"}]}]}""",
            json);
    }

    [Fact]
    public void WritesToolsAndToolChoice()
    {
        string json = Convert("""
            {"model":"m","messages":[],
             "tools":[{"name":"weather","description":"查天气","input_schema":{"type":"object","properties":{"city":{"type":"string"}}}}],
             "tool_choice":{"type":"tool","name":"weather","disable_parallel_tool_use":true}}
            """);

        Assert.Equal(
            """{"model":"gpt-4o","messages":[],"tools":[{"type":"function","function":{"name":"weather","description":"查天气","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}],"tool_choice":{"type":"function","function":{"name":"weather"}},"parallel_tool_calls":false}""",
            json);
    }

    [Theory]
    [InlineData("auto", "\"tool_choice\":\"auto\"")]
    [InlineData("any", "\"tool_choice\":\"required\"")]
    [InlineData("none", "\"tool_choice\":\"none\"")]
    public void MapsToolChoiceModes(string type, string expected)
    {
        string json = Convert("{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"" + type + "\"}}");

        Assert.Contains(expected, json, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesDocumentsAsFileParts()
    {
        string json = Convert("""
            {"model":"m","messages":[{"role":"user","content":[{"type":"document","title":"r.pdf","source":{"type":"base64","media_type":"application/pdf","data":"JVBE"}},{"type":"text","text":"总结"}]}]}
            """);

        Assert.Contains(
            """{"type":"file","file":{"filename":"r.pdf","file_data":"data:application/pdf;base64,JVBE"}}""",
            json,
            StringComparison.Ordinal);
    }
}
