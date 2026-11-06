using System.Text;
using System.Text.Json;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Protocol.Tests.OpenAiResponses;

/// <summary>Responses 请求读成 IR、IR 写成 Responses 请求（Q184、Q186、Q187、Q188、Q189、Q192、Q196）。</summary>
public sealed class ResponsesRequestTests
{
    private static ResponsesRequestConversion ToIr(string json) => ResponsesRequestConverter.ToIr(Encoding.UTF8.GetBytes(json));

    private static ResponsesRequestReading Read(string json) => ResponsesRequestConverter.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void RoutingFieldsAreReadWithoutAFullConversion()
    {
        ResponsesRequestReading reading = Read("""{"model":"gpt-x","stream":true,"previous_response_id":"resp_1","input":"hi"}""");

        Assert.True(reading.IsValid);
        Assert.Equal("gpt-x", reading.Model);
        Assert.True(reading.IsStream);
        Assert.Equal("resp_1", reading.PreviousResponseId);
        Assert.Null(reading.Rejected);
    }

    [Theory]
    [InlineData("""{"model":"m","input":[{"type":"item_reference","id":"msg_1"}]}""", "item_reference")]
    [InlineData("""{"model":"m","input":"hi","conversation":"conv_1","previous_response_id":"resp_1"}""", "只能用一个")]
    [InlineData("""{"model":"m","input":"hi","conversation":42}""", "conversation")]
    public void StatefulUsesTheGatewayCannotPlaceAreRejected(string json, string mention)
    {
        Assert.Contains(mention, Read(json).Rejected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"model":"m","input":"hi","background":true}""", null, true)]
    [InlineData("""{"model":"m","input":"hi","conversation":"conv_1"}""", "conv_1", false)]
    [InlineData("""{"model":"m","input":"hi","conversation":{"id":"conv_2"}}""", "conv_2", false)]
    [InlineData("""{"model":"m","conversation":"conv_1","input":[{"type":"item_reference","id":"msg_1"}]}""", "conv_1", false)]
    [InlineData("""{"model":"m","previous_response_id":"resp_1","input":[{"type":"item_reference","id":"msg_1"}]}""", null, false)]
    public void BackgroundConversationAndAnchoredItemReferencesAreAccepted(string json, string? conversation, bool background)
    {
        ResponsesRequestReading reading = Read(json);

        Assert.Null(reading.Rejected);
        Assert.Equal(conversation, reading.Conversation);
        Assert.Equal(background, reading.IsBackground);
    }

    [Theory]
    [InlineData("""{"model":"m","input":"hi","background":true}""")]
    [InlineData("""{"model":"m","input":"hi","conversation":"conv_1"}""")]
    [InlineData("""{"model":"m","previous_response_id":"resp_1","input":[{"type":"item_reference","id":"msg_1"}]}""")]
    public void StatefulRequestsOnlyGoToResponsesUpstreams(string json)
    {
        ResponsesRequestConversion conversion = ResponsesRequestConverter.ToIr(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(conversion.Unrepresentable ?? conversion.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"input":"hi"}""")]
    [InlineData("""[1,2]""")]
    public void InvalidBodiesAreNotValid(string json)
    {
        Assert.False(Read(json).IsValid);
    }

    [Fact]
    public void InstructionsComeFirstThenSystemAndDeveloperMessagesInOrder()
    {
        IrRequest ir = ToIr("""
            {"model":"m","instructions":"甲",
             "input":[
               {"role":"developer","content":"乙"},
               {"role":"user","content":[{"type":"input_text","text":"问"}]},
               {"type":"message","role":"system","content":"丙"}
             ]}
            """).Request!;

        Assert.Equal(["甲", "乙", "丙"], ir.System);
        IrMessage message = Assert.Single(ir.Messages);
        Assert.Equal(IrRole.User, message.Role);
        Assert.Equal("问", Assert.IsType<IrText>(Assert.Single(message.Content)).Text);
    }

    [Fact]
    public void AStringInputIsOneUserMessage()
    {
        IrRequest ir = ToIr("""{"model":"m","input":"你好","max_output_tokens":64,"temperature":1.5,"top_p":0.9}""").Request!;

        IrMessage message = Assert.Single(ir.Messages);
        Assert.Equal(IrRole.User, message.Role);
        Assert.Equal("你好", Assert.IsType<IrText>(Assert.Single(message.Content)).Text);
        Assert.Equal(64, ir.MaxOutputTokens);
        Assert.Equal(1.5, ir.Temperature);
        Assert.Equal(0.9, ir.TopP);
    }

    [Fact]
    public void FunctionCallsOutputsAndReasoningBecomeIrBlocks()
    {
        IrRequest ir = ToIr("""
            {"model":"m","input":[
              {"role":"user","content":"天气"},
              {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"查一下"}],"encrypted_content":"enc"},
              {"type":"function_call","call_id":"call_1","name":"get","arguments":"{\"c\":1}"},
              {"type":"function_call_output","call_id":"call_1","output":"晴"},
              {"type":"function_call_output","call_id":"call_2","output":[{"type":"input_text","text":"雨"}]}
            ],
            "tools":[{"type":"function","name":"get","description":"查","parameters":{"type":"object"},"strict":true}],
            "tool_choice":{"type":"function","name":"get"},
            "parallel_tool_calls":false}
            """).Request!;

        Assert.Equal(3, ir.Messages.Count);

        IrMessage assistant = ir.Messages[1];
        Assert.Equal(IrRole.Assistant, assistant.Role);
        IrReasoning reasoning = Assert.IsType<IrReasoning>(assistant.Content[0]);
        Assert.Equal("查一下", reasoning.Text);
        Assert.True(reasoning.SignedFor(ApiFormat.OpenAiResponses));
        Assert.Equal("rs_1", reasoning.ItemId);
        IrToolCall call = Assert.IsType<IrToolCall>(assistant.Content[1]);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("""{"c":1}""", call.ArgumentsJson);

        // 连着的两个 function_call_output 并成一条 user 消息。
        IrMessage results = ir.Messages[2];
        Assert.Equal(IrRole.User, results.Role);
        Assert.Equal(["call_1", "call_2"], results.Content.Cast<IrToolResult>().Select(r => r.ToolCallId));

        IrTool tool = Assert.Single(ir.Tools);
        Assert.Equal("get", tool.Name);
        Assert.Equal(IrToolChoiceMode.Specific, ir.ToolChoice!.Mode);
        Assert.Equal("get", ir.ToolChoice.Name);
        Assert.True(ir.ToolChoice.DisableParallelToolUse);
    }

    [Fact]
    public void StructuredOutputLogprobsAndEffortAreCarried()
    {
        IrRequest ir = ToIr("""
            {"model":"m","input":"hi",
             "text":{"format":{"type":"json_schema","name":"r","schema":{"type":"object"},"strict":true}},
             "top_logprobs":2,
             "reasoning":{"effort":"high"}}
            """).Request!;

        Assert.Equal(IrResponseFormatKind.JsonSchema, ir.ResponseFormat!.Kind);
        Assert.Equal("r", ir.ResponseFormat.Name);
        Assert.Equal(2, ir.Logprobs);
        Assert.Equal("high", ir.ReasoningEffort);

        // Anthropic 上游表达不了结构化输出和 logprobs（ADR-0014），Chat 都能。
        Assert.NotNull(IrTargetCheck.Unrepresentable(ir, ApiFormat.Anthropic));
        Assert.Null(IrTargetCheck.Unrepresentable(ir, ApiFormat.OpenAiChat));
    }

    [Theory]
    [InlineData("""{"model":"m","input":"hi","previous_response_id":"resp_1"}""")]
    [InlineData("""{"model":"m","input":"hi","tools":[{"type":"web_search"}]}""")]
    [InlineData("""{"model":"m","input":[{"role":"user","content":[{"type":"input_image","file_id":"file_1","detail":"auto"}]}]}""")]
    [InlineData("""{"model":"m","input":[{"type":"web_search_call","id":"ws_1","status":"completed"}]}""")]
    [InlineData("""{"model":"m","input":"hi","prompt":{"id":"pmpt_1"}}""")]
    public void ThingsOnlyAResponsesUpstreamUnderstandsAreUnrepresentableElsewhere(string json)
    {
        ResponsesRequestConversion conversion = ToIr(json);

        Assert.Null(conversion.Request);
        Assert.NotNull(conversion.Unrepresentable);
    }

    [Fact]
    public void TheWriterJoinsSystemIntoInstructionsAndNeverAsksTheUpstreamToStore()
    {
        var ir = new IrRequest
        {
            Model = "client-model",
            System = ["甲", "乙"],
            Messages =
            [
                new IrMessage(IrRole.User, [new IrText("问")]),
                new IrMessage(IrRole.Assistant,
                [
                    new IrReasoning("想过", "anthropic-sig", ApiFormat.Anthropic),
                    new IrReasoning("也想过", "enc-1", ApiFormat.OpenAiResponses, "rs_9"),
                    new IrToolCall("call_1", "get", """{"c":1}"""),
                ]),
                new IrMessage(IrRole.User, [new IrToolResult("call_1", [new IrText("晴")], IsError: false)]),
            ],
            MaxOutputTokens = 100,
            Logprobs = 0,
            Stream = true,
            EndUserId = "u1",
        };

        byte[] body = ResponsesRequestWriter.Write(ir, "gpt-upstream", "abc123");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal("gpt-upstream", root.GetProperty("model").GetString());
        Assert.Equal("甲\n\n乙", root.GetProperty("instructions").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(
            ["reasoning.encrypted_content", "message.output_text.logprobs"],
            root.GetProperty("include").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("abc123", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(100, root.GetProperty("max_output_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());

        JsonElement[] input = [.. root.GetProperty("input").EnumerateArray()];
        Assert.DoesNotContain(input, item => item.TryGetProperty("role", out JsonElement role) && role.GetString() is "system" or "developer");

        // 只有 Responses 自己签的推理项带回去；Anthropic 的签名对它没有意义（Q186）。
        JsonElement reasoning = Assert.Single(input, item => item.GetProperty("type").GetString() == "reasoning");
        Assert.Equal("rs_9", reasoning.GetProperty("id").GetString());
        Assert.Equal("enc-1", reasoning.GetProperty("encrypted_content").GetString());

        Assert.Contains(input, item => item.GetProperty("type").GetString() == "function_call"
            && item.GetProperty("call_id").GetString() == "call_1");
        Assert.Contains(input, item => item.GetProperty("type").GetString() == "function_call_output"
            && item.GetProperty("output").GetString() == "晴");
    }
}
