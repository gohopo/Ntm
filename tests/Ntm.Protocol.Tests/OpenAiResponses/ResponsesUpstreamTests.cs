using System.Buffers;
using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Protocol.Tests.OpenAiResponses;

/// <summary>Responses 上游的响应：流式与非流式的检视、读成 IR、经 IR 写成另外两种格式（Q190、Q191、Q193、Q195、ADR-0015）。</summary>
public sealed class ResponsesUpstreamTests
{
    private const string Created =
        """{"type":"response.created","sequence_number":0,"response":{"id":"resp_up1","object":"response","status":"in_progress","model":"gpt-real","output":[],"usage":null}}""";

    private const string InProgress =
        """{"type":"response.in_progress","sequence_number":1,"response":{"id":"resp_up1","object":"response","status":"in_progress","model":"gpt-real","output":[],"usage":null}}""";

    private const string MessageAdded =
        """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"in_progress","content":[]}}""";

    private const string PartAdded =
        """{"type":"response.content_part.added","sequence_number":3,"item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""";

    private const string TextDelta =
        """{"type":"response.output_text.delta","sequence_number":4,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"你好","logprobs":[]}""";

    private const string TextDone =
        """{"type":"response.output_text.done","sequence_number":5,"item_id":"msg_1","output_index":0,"content_index":0,"text":"你好"}""";

    private const string PartDone =
        """{"type":"response.content_part.done","sequence_number":6,"item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":"你好","annotations":[]}}""";

    private const string MessageDone =
        """{"type":"response.output_item.done","sequence_number":7,"output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"你好","annotations":[]}]}}""";

    private const string Completed =
        """{"type":"response.completed","sequence_number":8,"response":{"id":"resp_up1","object":"response","status":"completed","model":"gpt-real","output":[],"usage":{"input_tokens":30,"input_tokens_details":{"cached_tokens":10,"cache_write_tokens":5},"output_tokens":7,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":37}}}""";

    private const string ReasoningAdded =
        """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}""";

    private const string ReasoningDelta =
        """{"type":"response.reasoning_summary_text.delta","sequence_number":3,"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"想想"}""";

    private const string ToolAdded =
        """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_9","name":"get","arguments":"","status":"in_progress"}}""";

    private const string ToolDelta =
        """{"type":"response.function_call_arguments.delta","sequence_number":3,"item_id":"fc_1","output_index":0,"delta":"{\"city\":1}"}""";

    private const string ToolDone =
        """{"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_9","name":"get","arguments":"{\"city\":1}","status":"completed"}}""";

    private static StreamFrameVerdict Inspect(ResponsesStreamInspector inspector, string data) =>
        inspector.Inspect(Encoding.UTF8.GetBytes(data));

    [Fact]
    public void TheStreamCommitsOnTextAndReadsUsageFromResponseCompleted()
    {
        var inspector = new ResponsesStreamInspector();

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, Created));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, InProgress));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, MessageAdded));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, PartAdded));
        Assert.False(inspector.HasContentEvent);

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, TextDelta));
        Assert.Equal(StreamFrameVerdict.Terminal, Inspect(inspector, Completed));

        Assert.True(inspector.HasContentEvent);
        Assert.True(inspector.SawTerminal);
        Assert.Equal("resp_up1", inspector.ResponseId);
        Assert.Equal("gpt-real", inspector.UpstreamModel);
        Assert.Equal(2, inspector.OutputWideChars);

        // input_tokens 含缓存读写：网关的「非缓存输入」= 30 − 10 − 5（Q190）。
        Assert.Equal(15, inspector.Usage.InputTokens);
        Assert.Equal(10, inspector.Usage.CacheReadTokens);
        Assert.Equal(5, inspector.Usage.CacheWrite5mTokens);
        Assert.Equal(7, inspector.Usage.OutputTokens);
        Assert.Equal(UsageSource.Upstream, inspector.Usage.Source);
        Assert.False(inspector.SawThinking);
    }

    [Fact]
    public void ReasoningSummaryDeltasCountAsThinkingAndAsContent()
    {
        var inspector = new ResponsesStreamInspector();
        Inspect(inspector, Created);
        Inspect(inspector, ReasoningAdded);

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, ReasoningDelta));
        Assert.True(inspector.SawThinking);
    }

    [Fact]
    public void ReasoningTokensAloneAlsoCountAsThinking()
    {
        var inspector = new ResponsesStreamInspector();
        Inspect(inspector, Created);
        Inspect(inspector, TextDelta);
        Inspect(inspector, Completed.Replace("\"reasoning_tokens\":0", "\"reasoning_tokens\":12", StringComparison.Ordinal));

        Assert.True(inspector.SawThinking);
    }

    [Fact]
    public void AFunctionCallItemIsContentAsSoonAsItIsAdded()
    {
        var inspector = new ResponsesStreamInspector();
        Inspect(inspector, Created);

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, ToolAdded));
        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, ToolDelta));
    }

    [Theory]
    [InlineData("""{"type":"error","sequence_number":3,"code":"server_error","message":"boom","param":null}""")]
    [InlineData("""{"type":"error","sequence_number":3,"error":{"type":"server_error","code":"server_error","message":"boom"}}""")]
    [InlineData("""{"type":"response.failed","sequence_number":3,"response":{"id":"resp_up1","status":"failed","error":{"code":"server_error","message":"boom"},"output":[],"usage":null}}""")]
    public void BothErrorShapesAndResponseFailedAreErrors(string frame)
    {
        var inspector = new ResponsesStreamInspector();
        Inspect(inspector, Created);

        Assert.Equal(StreamFrameVerdict.Error, Inspect(inspector, frame));
        Assert.Contains("boom", inspector.ErrorPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoneMarkerIsTolerated()
    {
        var inspector = new ResponsesStreamInspector();
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, "[DONE]"));
    }

    [Fact]
    public void ACompletedStreamWithNoOutputEndedEmpty()
    {
        var inspector = new ResponsesStreamInspector();
        Inspect(inspector, Created);
        Inspect(inspector, Completed.Replace("\"output_tokens\":7", "\"output_tokens\":0", StringComparison.Ordinal));

        Assert.True(inspector.EndedEmpty);
    }

    [Fact]
    public void ANonStreamResponseIsReadIntoTheIr()
    {
        const string body = """
            {"id":"resp_up2","object":"response","status":"completed","model":"gpt-real",
             "output":[
               {"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"先想"}],"encrypted_content":"enc-1"},
               {"id":"msg_1","type":"message","role":"assistant","content":[{"type":"output_text","text":"答案","annotations":[]}]},
               {"id":"fc_1","type":"function_call","call_id":"call_1","name":"get","arguments":"{\"a\":1}"},
               {"id":"ws_1","type":"web_search_call","status":"completed"}
             ],
             "usage":{"input_tokens":12,"input_tokens_details":{"cached_tokens":2},"output_tokens":4,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":16}}
            """;

        ResponsesResponseReading reading = ResponsesResponseInspector.Read(Encoding.UTF8.GetBytes(body));

        Assert.False(reading.IsError);
        Assert.True(reading.HasOutput);
        Assert.True(reading.SawThinking);
        Assert.Equal("resp_up2", reading.ResponseId);
        Assert.Equal(10, reading.Usage.InputTokens);
        Assert.Equal(2, reading.Usage.CacheReadTokens);

        IrResponse response = Assert.IsType<IrResponse>(reading.Response);
        Assert.Collection(
            response.Content,
            block =>
            {
                IrReasoning reasoning = Assert.IsType<IrReasoning>(block);
                Assert.Equal("先想", reasoning.Text);
                Assert.Equal("enc-1", reasoning.Signature);
                Assert.Equal("rs_1", reasoning.ItemId);
                Assert.True(reasoning.SignedFor(ApiFormat.OpenAiResponses));
                Assert.False(reasoning.SignedFor(ApiFormat.Anthropic));
            },
            block => Assert.Equal("答案", Assert.IsType<IrText>(block).Text),
            block =>
            {
                IrToolCall call = Assert.IsType<IrToolCall>(block);
                Assert.Equal("call_1", call.Id);
                Assert.Equal("get", call.Name);
                Assert.Equal("""{"a":1}""", call.ArgumentsJson);
            });
        Assert.Equal(IrStopReason.ToolUse, response.StopReason);
    }

    [Fact]
    public void ARefusalAndAnIncompleteResponseKeepTheirMeaning()
    {
        const string refusal = """
            {"id":"resp_r","status":"completed","model":"m","output":[{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"refusal","refusal":"不行"}]}],
             "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
            """;
        IrResponse refused = ResponsesResponseInspector.Read(Encoding.UTF8.GetBytes(refusal)).Response!;
        IrText text = Assert.IsType<IrText>(Assert.Single(refused.Content));
        Assert.True(text.IsRefusal);
        Assert.Equal(IrStopReason.Refusal, refused.StopReason);

        const string cut = """
            {"id":"resp_c","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"m",
             "output":[{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"output_text","text":"半","annotations":[]}]}],
             "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
            """;
        Assert.Equal(IrStopReason.MaxTokens, ResponsesResponseInspector.Read(Encoding.UTF8.GetBytes(cut)).Response!.StopReason);
    }

    [Fact]
    public void AnErrorBodyIsAnError()
    {
        ResponsesResponseReading reading = ResponsesResponseInspector.Read(
            """{"error":{"type":"invalid_request_error","code":"bad","message":"nope","param":null}}"""u8);

        Assert.True(reading.IsError);
        Assert.Contains("nope", reading.ErrorPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResponseWriterGroupsTextIntoOneMessageAndReportsTotalInput()
    {
        var response = new IrResponse
        {
            Model = "gpt-real",
            Content =
            [
                new IrReasoning("想", "sig-anthropic", ApiFormat.Anthropic),
                new IrText("一"),
                new IrText("二"),
                new IrToolCall("call_1", "get", """{"a":1}"""),
            ],
            StopReason = IrStopReason.ToolUse,
            Usage = new Usage { InputTokens = 3, CacheReadTokens = 2, CacheWrite5mTokens = 1, OutputTokens = 4, Source = UsageSource.Upstream },
        };

        ResponsesResponseWriting writing = ResponsesResponseWriter.Write(response, "resp_abc", "fallback", 1_700_000_000);

        using JsonDocument document = JsonDocument.Parse(writing.Body!);
        JsonElement root = document.RootElement;
        Assert.Equal("resp_abc", root.GetProperty("id").GetString());
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("gpt-real", root.GetProperty("model").GetString());

        JsonElement[] output = [.. root.GetProperty("output").EnumerateArray()];
        Assert.Equal(["reasoning", "message", "function_call"], output.Select(o => o.GetProperty("type").GetString()));

        // 签名是 Anthropic 的：Responses 上游认不得，只留文字（Q186）。
        Assert.False(output[0].TryGetProperty("encrypted_content", out _));
        Assert.Equal(2, output[1].GetProperty("content").GetArrayLength());
        Assert.Equal("call_1", output[2].GetProperty("call_id").GetString());

        JsonElement usage = root.GetProperty("usage");
        Assert.Equal(6, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64());
        Assert.Equal(10, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public void AResponsesStreamTranslatesIntoChatChunks()
    {
        using var translator = new IrStreamTranslator(
            new ResponsesStreamEventReader(),
            new OpenAiChatStreamEventWriter("chatcmpl-1", "fallback", 1_700_000_000, includeUsage: true));
        var output = new ArrayBufferWriter<byte>();

        foreach (string frame in new[] { Created, InProgress, MessageAdded, PartAdded, TextDelta, TextDone, PartDone, MessageDone, Completed })
        {
            translator.Translate(Encoding.UTF8.GetBytes(frame), output);
        }

        translator.Complete(new Usage { InputTokens = 15, CacheReadTokens = 10, CacheWrite5mTokens = 5, OutputTokens = 7, Source = UsageSource.Upstream }, output);

        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        List<JsonElement> chunks = ChatChunks(text);
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal("gpt-real", chunks[0].GetProperty("model").GetString());
        Assert.Contains(chunks, c => c.GetProperty("choices").GetArrayLength() > 0
            && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out JsonElement content)
            && content.GetString() == "你好");
        Assert.Contains(chunks, c => c.GetProperty("choices").GetArrayLength() > 0
            && c.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == "stop");
        Assert.Equal(30, chunks[^1].GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AResponsesStreamWithAToolCallTranslatesIntoAnthropicEvents()
    {
        using var translator = new IrStreamTranslator(
            new ResponsesStreamEventReader(),
            new AnthropicStreamEventWriter("msg_x", "fallback"));
        var output = new ArrayBufferWriter<byte>();

        foreach (string frame in new[] { Created, ToolAdded, ToolDelta, ToolDone, Completed })
        {
            translator.Translate(Encoding.UTF8.GetBytes(frame), output);
        }

        translator.Complete(new Usage { InputTokens = 1, OutputTokens = 2, Source = UsageSource.Upstream }, output);

        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Contains("\"type\":\"tool_use\",\"id\":\"call_9\",\"name\":\"get\"", text, StringComparison.Ordinal);
        Assert.Contains("\"partial_json\":\"{\\\"city\\\":1}\"", text, StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"tool_use\"", text, StringComparison.Ordinal);
        Assert.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChatStreamTranslatesIntoResponsesEventsWithSequenceNumbersAndNoDoneMarker()
    {
        using var translator = new IrStreamTranslator(
            new OpenAiChatStreamEventReader(),
            new ResponsesStreamEventWriter("resp_gw1", "fallback", 1_700_000_000));
        var output = new ArrayBufferWriter<byte>();

        string[] frames =
        [
            """{"id":"c1","object":"chat.completion.chunk","model":"gpt-chat","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"想"}}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"gpt-chat","choices":[{"index":0,"delta":{"content":"你好"}}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"gpt-chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]",
        ];
        foreach (string frame in frames)
        {
            translator.Translate(Encoding.UTF8.GetBytes(frame), output);
        }

        translator.Complete(new Usage { InputTokens = 5, OutputTokens = 3, Source = UsageSource.Upstream }, output);

        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.DoesNotContain("[DONE]", text, StringComparison.Ordinal);

        List<JsonElement> events = ResponsesEvents(text);
        Assert.Equal("response.created", events[0].GetProperty("type").GetString());
        Assert.Equal("response.completed", events[^1].GetProperty("type").GetString());
        Assert.Equal(
            Enumerable.Range(0, events.Count),
            events.Select(e => e.GetProperty("sequence_number").GetInt32()));

        Assert.Contains(events, e => e.GetProperty("type").GetString() == "response.reasoning_summary_text.delta"
            && e.GetProperty("delta").GetString() == "想");
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "response.output_text.delta"
            && e.GetProperty("delta").GetString() == "你好");

        JsonElement done = events[^1].GetProperty("response");
        Assert.Equal("resp_gw1", done.GetProperty("id").GetString());
        Assert.Equal("gpt-chat", done.GetProperty("model").GetString());
        Assert.Equal(5, done.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.Equal(
            ["reasoning", "message"],
            done.GetProperty("output").EnumerateArray().Select(o => o.GetProperty("type").GetString()));
    }

    [Fact]
    public void TheResponsesStreamErrorEventHasCodeAndMessage()
    {
        var output = new ArrayBufferWriter<byte>();
        ResponsesStreamEventWriter.WriteError(output, "upstream_failed", "上游挂了");

        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.StartsWith("event: error\ndata: ", text, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(text["event: error\ndata: ".Length..].Trim());
        Assert.Equal("error", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("upstream_failed", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("上游挂了", document.RootElement.GetProperty("message").GetString());
    }

    private static List<JsonElement> ChatChunks(string text) =>
        [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .Where(frame => frame.StartsWith("data: ", StringComparison.Ordinal) && frame != "data: [DONE]")
            .Select(frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.Clone())];

    private static List<JsonElement> ResponsesEvents(string text) =>
        [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal)))
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone())];
}
