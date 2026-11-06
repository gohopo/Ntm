using System.Text;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Tests.Anthropic;

public sealed class AnthropicRequestReaderTests
{
    private static AnthropicRequestReading Read(string json) => AnthropicRequestReader.Read(Encoding.UTF8.GetBytes(json));

    private static IrRequest ReadValid(string json)
    {
        AnthropicRequestReading reading = Read(json);
        Assert.True(reading.IsValid, reading.Error);
        return reading.Request!;
    }

    [Fact]
    public void ReadsStringContentSystemAndSamplingParameters()
    {
        IrRequest request = ReadValid("""
            {"model":"claude-x","max_tokens":256,"stream":true,"temperature":0.3,"top_p":0.9,
             "stop_sequences":["END"],"system":"be brief","metadata":{"user_id":"u-1"},
             "messages":[{"role":"user","content":"你好"}]}
            """);

        Assert.Equal("claude-x", request.Model);
        Assert.Equal(256, request.MaxOutputTokens);
        Assert.True(request.Stream);
        Assert.Equal(0.3, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(["END"], request.StopSequences);
        Assert.Equal(["be brief"], request.System);
        Assert.Equal("u-1", request.EndUserId);

        IrMessage message = Assert.Single(request.Messages);
        Assert.Equal(IrRole.User, message.Role);
        Assert.Equal(new IrText("你好"), Assert.Single(message.Content));
    }

    [Fact]
    public void ReadsSystemAsTextBlocks()
    {
        IrRequest request = ReadValid("""
            {"model":"m","system":[{"type":"text","text":"a","cache_control":{"type":"ephemeral"}},{"type":"text","text":"b"}],
             "messages":[]}
            """);

        Assert.Equal(["a", "b"], request.System);
    }

    [Fact]
    public void KeepsBlockOrderToolCallsAndRawArguments()
    {
        IrRequest request = ReadValid("""
            {"model":"m","messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"plan","signature":"sig-1"},
                {"type":"text","text":"let me check"},
                {"type":"tool_use","id":"toolu_1","name":"weather","input":{"city":"北京","days":2}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_1","content":"晴","is_error":false},
                {"type":"text","text":"thanks"}]}]}
            """);

        IReadOnlyList<IrBlock> assistant = request.Messages[0].Content;
        Assert.Equal(new IrReasoning("plan", "sig-1", ApiFormat.Anthropic), assistant[0]);
        Assert.Equal(new IrText("let me check"), assistant[1]);
        var call = Assert.IsType<IrToolCall>(assistant[2]);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("weather", call.Name);
        Assert.Equal("""{"city":"北京","days":2}""", call.ArgumentsJson);

        var result = Assert.IsType<IrToolResult>(request.Messages[1].Content[0]);
        Assert.Equal("toolu_1", result.ToolCallId);
        Assert.False(result.IsError);
        Assert.Equal(new IrText("晴"), Assert.Single(result.Content));
        Assert.Equal(new IrText("thanks"), request.Messages[1].Content[1]);
    }

    [Fact]
    public void ToolUseWithoutInputGetsEmptyObject()
    {
        IrRequest request = ReadValid("""
            {"model":"m","messages":[{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"now"}]}]}
            """);

        Assert.Equal("{}", Assert.IsType<IrToolCall>(request.Messages[0].Content[0]).ArgumentsJson);
    }

    [Fact]
    public void ReadsImagesFromBase64AndUrl()
    {
        IrRequest request = ReadValid("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBOR"}},
              {"type":"image","source":{"type":"url","url":"https://example.com/a.png"}}]}]}
            """);

        Assert.Equal(new IrImage("image/png", "iVBOR", null), request.Messages[0].Content[0]);
        Assert.Equal(new Uri("https://example.com/a.png"), Assert.IsType<IrImage>(request.Messages[0].Content[1]).Url);
    }

    [Fact]
    public void ReadsRedactedThinking()
    {
        IrRequest request = ReadValid("""
            {"model":"m","messages":[{"role":"assistant","content":[{"type":"redacted_thinking","data":"opaque"}]}]}
            """);

        Assert.Equal(new IrRedactedReasoning("opaque"), request.Messages[0].Content[0]);
    }

    [Fact]
    public void ReadsToolsAndSkipsServerToolsWithoutSchema()
    {
        IrRequest request = ReadValid("""
            {"model":"m","messages":[],
             "tools":[{"name":"weather","description":"查天气","input_schema":{"type":"object","properties":{}}},
                      {"type":"web_search_20250305","name":"web_search","max_uses":3}],
             "tool_choice":{"type":"tool","name":"weather","disable_parallel_tool_use":true}}
            """);

        IrTool tool = Assert.Single(request.Tools);
        Assert.Equal("weather", tool.Name);
        Assert.Equal("查天气", tool.Description);
        Assert.Equal("""{"type":"object","properties":{}}""", tool.ParametersSchemaJson);
        Assert.Equal(new IrToolChoice(IrToolChoiceMode.Specific, "weather", true), request.ToolChoice);
    }

    [Fact]
    public void MarksServerToolsAsAnthropicOnly()
    {
        AnthropicRequestReading reading = Read("""
            {"model":"m","messages":[],"tools":[{"type":"web_search_20250305","name":"web_search","max_uses":3}]}
            """);

        Assert.True(reading.IsValid, reading.Error);
        Assert.Contains("web_search_20250305", reading.AnthropicOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsServerToolHistoryBlocksButMarksThemAnthropicOnly()
    {
        AnthropicRequestReading reading = Read("""
            {"model":"m","messages":[
              {"role":"user","content":"搜一下"},
              {"role":"assistant","content":[
                {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"x"}},
                {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[]},
                {"type":"text","text":"结果","citations":[{"type":"web_search_result_location","url":"https://a"}]}]},
              {"role":"user","content":"继续"}]}
            """);

        Assert.True(reading.IsValid, reading.Error);
        Assert.Contains("server_tool_use", reading.AnthropicOnly, StringComparison.Ordinal);
        Assert.Equal(new IrText("结果"), Assert.Single(reading.Request!.Messages[1].Content));
    }

    [Fact]
    public void PlainRequestsAreNotAnthropicOnly()
    {
        Assert.Null(Read("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""").AnthropicOnly);
    }

    [Theory]
    [InlineData("auto", IrToolChoiceMode.Auto)]
    [InlineData("any", IrToolChoiceMode.Any)]
    [InlineData("none", IrToolChoiceMode.None)]
    public void ReadsToolChoiceModes(string type, IrToolChoiceMode mode)
    {
        IrRequest request = ReadValid("{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"" + type + "\"}}");

        Assert.Equal(mode, request.ToolChoice!.Mode);
    }

    [Theory]
    [InlineData("""{"messages":[]}""", "缺少 model")]
    [InlineData("""{"model":"m"}""", "缺少 messages")]
    [InlineData("""[1,2]""", "必须是 JSON 对象")]
    [InlineData("""{"model":"m","messages":[{"role":"system","content":"x"}]}""", "role")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"fancy_block","id":"x"}]}]}""", "fancy_block")]
    [InlineData("""{"model":"m","max_tokens":1.5,"messages":[]}""", "max_tokens")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":42}]}""", "content")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"text"}]}]}""", "text")]
    public void RejectsInvalidRequestsWithAReadableReason(string json, string expected)
    {
        AnthropicRequestReading reading = Read(json);

        Assert.False(reading.IsValid);
        Assert.Contains(expected, reading.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.False(Read("{\"model\":").IsValid);
        Assert.False(AnthropicRequestReader.Read(ReadOnlyMemory<byte>.Empty).IsValid);
    }

    [Fact]
    public void RejectsUnexpectedBlocksInsideToolResult()
    {
        AnthropicRequestReading reading = Read("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"t","content":[{"type":"tool_use","id":"x","name":"y"}]}]}]}
            """);

        Assert.False(reading.IsValid);
    }
}
