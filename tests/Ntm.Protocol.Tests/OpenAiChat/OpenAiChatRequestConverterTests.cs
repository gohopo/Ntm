using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

/// <summary>OpenAI Chat 请求 → IR → Anthropic Messages 请求（Q171–Q173）。</summary>
public sealed class OpenAiChatRequestConverterTests
{
    private static IrRequest ToIr(string json)
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(json));
        Assert.Null(conversion.Error);
        Assert.Null(conversion.Unrepresentable);
        return conversion.Request!;
    }

    private static JsonElement ToAnthropic(string json, string upstreamModel = "claude-real")
    {
        byte[] body = AnthropicRequestWriter.Write(ToIr(json), upstreamModel);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public void SystemAndDeveloperMessagesBecomeTheSystemBlocksInOrder()
    {
        JsonElement body = ToAnthropic("""
            {"model":"m","messages":[
              {"role":"system","content":"be brief"},
              {"role":"developer","content":[{"type":"text","text":"answer in 中文"}]},
              {"role":"user","content":"hi"}]}
            """);

        Assert.Equal("claude-real", body.GetProperty("model").GetString());
        string[] system = [.. body.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()!)];
        Assert.Equal(["be brief", "answer in 中文"], system);

        JsonElement message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hi", message.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void MaxTokensIsLeftOutWhenTheClientGaveNoneSoTheGatewayCanFillIt()
    {
        JsonElement body = ToAnthropic("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void MaxCompletionTokensWinsOverMaxTokens()
    {
        JsonElement body = ToAnthropic("""
            {"model":"m","max_tokens":10,"max_completion_tokens":20,"messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Equal(20, body.GetProperty("max_tokens").GetInt64());
    }

    [Fact]
    public void ConsecutiveToolMessagesBecomeOneUserMessageWithEveryResult()
    {
        JsonElement body = ToAnthropic("""
            {"model":"m","messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":null,"tool_calls":[
                {"id":"call_1","type":"function","function":{"name":"get","arguments":"{\"city\":\"北京\"}"}},
                {"id":"call_2","type":"function","function":{"name":"get","arguments":""}}]},
              {"role":"tool","tool_call_id":"call_1","content":"晴"},
              {"role":"tool","tool_call_id":"call_2","content":"雨"}]}
            """);

        JsonElement[] messages = [.. body.GetProperty("messages").EnumerateArray()];
        Assert.Equal(3, messages.Length);

        JsonElement[] calls = [.. messages[1].GetProperty("content").EnumerateArray()];
        Assert.Equal("tool_use", calls[0].GetProperty("type").GetString());
        Assert.Equal("北京", calls[0].GetProperty("input").GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Object, calls[1].GetProperty("input").ValueKind);

        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        JsonElement[] results = [.. messages[2].GetProperty("content").EnumerateArray()];
        Assert.Equal(["call_1", "call_2"], results.Select(r => r.GetProperty("tool_use_id").GetString()));
        Assert.All(results, r => Assert.Equal("tool_result", r.GetProperty("type").GetString()));
    }

    [Fact]
    public void ImagesKeepDataUrlsAsBase64AndHttpUrlsAsUrls()
    {
        JsonElement body = ToAnthropic("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,QUJD"}},
              {"type":"image_url","image_url":{"url":"https://example.com/a.png"}}]}]}
            """);

        JsonElement[] blocks = [.. body.GetProperty("messages")[0].GetProperty("content").EnumerateArray()];
        JsonElement first = blocks[0].GetProperty("source");
        Assert.Equal("base64", first.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", first.GetProperty("media_type").GetString());
        Assert.Equal("QUJD", first.GetProperty("data").GetString());

        JsonElement second = blocks[1].GetProperty("source");
        Assert.Equal("url", second.GetProperty("type").GetString());
        Assert.Equal("https://example.com/a.png", second.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("\"none\"", "none", null)]
    [InlineData("\"auto\"", "auto", null)]
    [InlineData("\"required\"", "any", null)]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":\"get\"}}", "tool", "get")]
    public void ToolChoiceIsMapped(string toolChoice, string expectedType, string? expectedName)
    {
        JsonElement body = ToAnthropic(
            "{\"model\":\"m\",\"tool_choice\":" + toolChoice + ",\"parallel_tool_calls\":false," +
            "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get\",\"parameters\":{\"type\":\"object\"}}}]," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        JsonElement choice = body.GetProperty("tool_choice");
        Assert.Equal(expectedType, choice.GetProperty("type").GetString());
        if (expectedName is not null)
        {
            Assert.Equal(expectedName, choice.GetProperty("name").GetString());
        }

        JsonElement tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("get", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void SamplingParametersAreCarriedOrClampedAndSamplingOnlyOnesAreDropped()
    {
        JsonElement body = ToAnthropic("""
            {"model":"m","temperature":1.7,"top_p":0.9,"stop":"END","user":"u-1","seed":7,"frequency_penalty":0.5,
             "messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Equal(1.0, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
        Assert.Equal(["END"], body.GetProperty("stop_sequences").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("u-1", body.GetProperty("metadata").GetProperty("user_id").GetString());
        Assert.False(body.TryGetProperty("seed", out _));
        Assert.False(body.TryGetProperty("frequency_penalty", out _));
    }

    [Theory]
    [InlineData("\"n\":2")]
    [InlineData("\"audio\":{\"voice\":\"alloy\",\"format\":\"wav\"}")]
    [InlineData("\"web_search_options\":{}")]
    [InlineData("\"modalities\":[\"image\",\"text\"]")]
    public void ResultShapingParametersMakeTheRequestUnrepresentable(string parameter)
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(
            "{\"model\":\"m\"," + parameter + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));

        Assert.Null(conversion.Request);
        Assert.Null(conversion.Error);
        Assert.False(string.IsNullOrEmpty(conversion.Unrepresentable));
    }

    /// <summary>logprobs 和结构化输出 IR 装得下，Responses 上游也能接；只有 Anthropic 接不了（Q188/ADR-0014）。</summary>
    [Theory]
    [InlineData("\"logprobs\":true")]
    [InlineData("\"response_format\":{\"type\":\"json_object\"}")]
    [InlineData("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"x\",\"schema\":{}}}")]
    public void LogprobsAndStructuredOutputOnlyRuleOutAnthropic(string parameter)
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(
            "{\"model\":\"m\"," + parameter + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));

        IrRequest ir = Assert.IsType<IrRequest>(conversion.Request);
        Assert.Null(conversion.Unrepresentable);
        Assert.False(string.IsNullOrEmpty(IrTargetCheck.Unrepresentable(ir, ApiFormat.Anthropic)));
        Assert.Null(IrTargetCheck.Unrepresentable(ir, ApiFormat.OpenAiResponses));
        Assert.Null(IrTargetCheck.Unrepresentable(ir, ApiFormat.OpenAiChat));
    }

    [Fact]
    public void StructuredOutputAndLogprobsAreReadIntoTheIr()
    {
        IrRequest ir = ToIr("""
            {"model":"m","logprobs":true,"top_logprobs":3,"reasoning_effort":"high",
             "response_format":{"type":"json_schema","json_schema":{"name":"weather","description":"d","schema":{"type":"object"},"strict":true}},
             "messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Equal(3, ir.Logprobs);
        Assert.Equal("high", ir.ReasoningEffort);
        Assert.Equal(
            new IrResponseFormat(IrResponseFormatKind.JsonSchema, "weather", "d", "{\"type\":\"object\"}", true),
            ir.ResponseFormat);
    }

    [Fact]
    public void StopSequencesRuleOutResponsesUpstreams()
    {
        IrRequest ir = ToIr("""{"model":"m","stop":"END","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Contains("停止序列", IrTargetCheck.Unrepresentable(ir, ApiFormat.OpenAiResponses), StringComparison.Ordinal);
        Assert.Null(IrTargetCheck.Unrepresentable(ir, ApiFormat.Anthropic));
    }

    [Fact]
    public void PlainTextResponseFormatAndSingleChoiceAreFine()
    {
        IrRequest ir = ToIr("""
            {"model":"m","n":1,"response_format":{"type":"text"},"messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Single(ir.Messages);
    }

    [Fact]
    public void ToolCallArgumentsThatAreNotJsonMakeTheRequestUnrepresentable()
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes("""
            {"model":"m","messages":[
              {"role":"assistant","tool_calls":[{"id":"c","type":"function","function":{"name":"f","arguments":"{oops"}}]},
              {"role":"tool","tool_call_id":"c","content":"x"}]}
            """));

        Assert.NotNull(conversion.Unrepresentable);
    }

    [Fact]
    public void InvalidJsonIsAnErrorNotUnrepresentable()
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr("{nope"u8);

        Assert.NotNull(conversion.Error);
        Assert.Null(conversion.Unrepresentable);
    }

    [Fact]
    public void TheMaxTokensPatchOnlyFillsWhenAbsent()
    {
        Assert.True(AnthropicRequestPatcher.TryEnsureMaxTokens("""{"model":"m","messages":[]}"""u8, 4096, out byte[] patched));
        Assert.Equal(4096, JsonDocument.Parse(patched).RootElement.GetProperty("max_tokens").GetInt64());

        Assert.False(AnthropicRequestPatcher.TryEnsureMaxTokens("""{"model":"m","max_tokens":10}"""u8, 4096, out _));
    }
}
