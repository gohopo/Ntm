using System.Buffers;
using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.Anthropic;

/// <summary>Anthropic 上游的响应：非流式与流式的检视、写回 OpenAI Chat（Q164、Q167、Q175、Q176）。</summary>
public sealed class AnthropicUpstreamTests
{
    private const string MessageStart =
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-real","content":[],"usage":{"input_tokens":20,"cache_read_input_tokens":5,"cache_creation_input_tokens":7,"cache_creation":{"ephemeral_5m_input_tokens":3,"ephemeral_1h_input_tokens":4},"output_tokens":1}}}""";

    private const string Ping = """{"type":"ping"}""";
    private const string TextStart = """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""";
    private const string TextDelta = """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你好"}}""";
    private const string ThinkingStart = """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""";
    private const string ThinkingDelta = """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"想想"}}""";
    private const string SignatureDelta = """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}""";
    private const string ToolStart = """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get","input":{}}}""";
    private const string ToolDelta = """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""";
    private const string BlockStop = """{"type":"content_block_stop","index":0}""";
    private const string MessageDelta = """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":9}}""";
    private const string MessageStop = """{"type":"message_stop"}""";
    private const string ErrorEvent = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";

    private static StreamFrameVerdict Inspect(AnthropicStreamInspector inspector, string data) =>
        inspector.Inspect(Encoding.UTF8.GetBytes(data));

    [Fact]
    public void TheStreamCommitsOnRealContentAndAddsInputFromMessageStartToOutputFromMessageDelta()
    {
        var inspector = new AnthropicStreamInspector();

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, MessageStart));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, Ping));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, TextStart));
        Assert.False(inspector.HasContentEvent);

        // message_start 之后中途断掉：输入已经是真数（Q177）。
        Assert.Equal(20, inspector.InputUsage.InputTokens);
        Assert.Equal(5, inspector.InputUsage.CacheReadTokens);
        Assert.Equal(3, inspector.InputUsage.CacheWrite5mTokens);
        Assert.Equal(4, inspector.InputUsage.CacheWrite1hTokens);

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, TextDelta));
        Assert.True(inspector.HasContentEvent);
        Assert.Equal(2, inspector.OutputWideChars);

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, BlockStop));
        Assert.Equal(StreamFrameVerdict.UsageOnly, Inspect(inspector, MessageDelta));
        Assert.Equal(StreamFrameVerdict.Terminal, Inspect(inspector, MessageStop));

        Assert.True(inspector.SawTerminal);
        Assert.Equal("end_turn", inspector.FinishReason);
        Assert.Equal("claude-real", inspector.UpstreamModel);
        Assert.Equal(20, inspector.Usage.InputTokens);
        Assert.Equal(9, inspector.Usage.OutputTokens);
        Assert.Equal(UsageSource.Upstream, inspector.Usage.Source);
        Assert.False(inspector.EndedEmpty);
    }

    [Fact]
    public void ThinkingIsContentAndMarksTheStreamButSignaturesAreNot()
    {
        var inspector = new AnthropicStreamInspector();
        Inspect(inspector, MessageStart);

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, ThinkingStart));
        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, ThinkingDelta));
        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(inspector, SignatureDelta));
        Assert.True(inspector.SawThinking);
    }

    [Fact]
    public void AToolUseStartIsContent()
    {
        var inspector = new AnthropicStreamInspector();
        Inspect(inspector, MessageStart);

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, ToolStart));
    }

    [Fact]
    public void AnErrorEventIsAnError()
    {
        var inspector = new AnthropicStreamInspector();
        Inspect(inspector, MessageStart);

        Assert.Equal(StreamFrameVerdict.Error, Inspect(inspector, ErrorEvent));
        Assert.Contains("overloaded_error", inspector.ErrorPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWithNothingButFramingEndsEmpty()
    {
        var inspector = new AnthropicStreamInspector();
        Inspect(inspector, MessageStart);
        Inspect(inspector, """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":0}}""");
        Inspect(inspector, MessageStop);

        Assert.True(inspector.EndedEmpty);
    }

    [Fact]
    public void ANonStreamResponseIsReadWithItsUsageSplitAndThinking()
    {
        AnthropicResponseReading reading = AnthropicResponseInspector.Read("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-real",
             "content":[{"type":"thinking","thinking":"嗯","signature":"s"},{"type":"text","text":"答"},
                        {"type":"tool_use","id":"toolu_1","name":"get","input":{"city":"北京"}}],
             "stop_reason":"tool_use",
             "usage":{"input_tokens":10,"cache_read_input_tokens":2,"cache_creation_input_tokens":6,"output_tokens":4}}
            """u8);

        Assert.False(reading.IsError);
        Assert.True(reading.HasOutput);
        Assert.True(reading.SawThinking);
        Assert.Equal("claude-real", reading.UpstreamModel);
        Assert.Equal(10, reading.Usage.InputTokens);
        Assert.Equal(2, reading.Usage.CacheReadTokens);

        // 没有 5m/1h 的拆分时，缓存写入全部按 5 分钟算（Q164）。
        Assert.Equal(6, reading.Usage.CacheWrite5mTokens);
        Assert.Equal(0, reading.Usage.CacheWrite1hTokens);
        Assert.Equal(4, reading.Usage.OutputTokens);
        Assert.NotNull(reading.Response);
    }

    [Fact]
    public void AnErrorBodyIsAnError()
    {
        AnthropicResponseReading reading = AnthropicResponseInspector.Read(
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad"}}"""u8);

        Assert.True(reading.IsError);
        Assert.Contains("invalid_request_error", reading.ErrorPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonStreamResponseIsWrittenAsAChatCompletion()
    {
        AnthropicResponseReading reading = AnthropicResponseInspector.Read("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-real",
             "content":[{"type":"thinking","thinking":"嗯","signature":"s"},{"type":"text","text":"前"},{"type":"text","text":"后"},
                        {"type":"tool_use","id":"toolu_1","name":"get","input":{"city":"北京"}}],
             "stop_reason":"tool_use",
             "usage":{"input_tokens":10,"cache_read_input_tokens":2,"cache_creation_input_tokens":6,"output_tokens":4}}
            """u8);

        OpenAiChatResponseWriting writing = OpenAiChatResponseWriter.Write(reading.Response!, "chatcmpl-1", "fallback", 1_700_000_000);
        Assert.Null(writing.Error);
        JsonElement root = JsonDocument.Parse(writing.Body!).RootElement;

        Assert.Equal("chatcmpl-1", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("claude-real", root.GetProperty("model").GetString());

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        JsonElement message = choice.GetProperty("message");
        Assert.Equal("前后", message.GetProperty("content").GetString());
        Assert.Equal("嗯", message.GetProperty("reasoning_content").GetString());

        JsonElement call = Assert.Single(message.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("toolu_1", call.GetProperty("id").GetString());
        Assert.Equal("get", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("北京", JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!)
            .RootElement.GetProperty("city").GetString());

        // prompt_tokens 是输入的总和；cached_tokens 只算缓存读（Q175）。
        JsonElement usage = root.GetProperty("usage");
        Assert.Equal(18, usage.GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("completion_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt64());
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("refusal", "content_filter")]
    public void StopReasonsMapToFinishReasons(string stopReason, string finishReason)
    {
        AnthropicResponseReading reading = AnthropicResponseInspector.Read(Encoding.UTF8.GetBytes(
            "{\"type\":\"message\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"stop_reason\":\"" + stopReason +
            "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"));

        OpenAiChatResponseWriting writing = OpenAiChatResponseWriter.Write(reading.Response!, "id", "m", 0);
        Assert.Equal(
            finishReason,
            JsonDocument.Parse(writing.Body!).RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    private static List<JsonElement> Chunks(string sse)
    {
        var chunks = new List<JsonElement>();
        foreach (string frame in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string data = frame["data: ".Length..];
            if (data != "[DONE]")
            {
                chunks.Add(JsonDocument.Parse(data).RootElement.Clone());
            }
        }

        return chunks;
    }

    private static string Translate(bool includeUsage, params string[] events)
    {
        var output = new ArrayBufferWriter<byte>();
        using var translator = new AnthropicToOpenAiChatStreamTranslator("chatcmpl-1", "fallback", 1_700_000_000, includeUsage);
        var inspector = new AnthropicStreamInspector();
        foreach (string data in events)
        {
            inspector.Inspect(Encoding.UTF8.GetBytes(data));
            translator.Translate(Encoding.UTF8.GetBytes(data), output);
        }

        translator.Complete(inspector.Usage, output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    [Fact]
    public void TheStreamBecomesChatCompletionChunksEndingWithDone()
    {
        string sse = Translate(
            false,
            MessageStart, Ping, ThinkingStart, ThinkingDelta, SignatureDelta, BlockStop,
            TextStart, TextDelta, BlockStop, ToolStart, ToolDelta, MessageDelta, MessageStop);

        Assert.EndsWith("data: [DONE]\n\n", sse, StringComparison.Ordinal);
        List<JsonElement> chunks = Chunks(sse);
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString()));
        Assert.All(chunks, c => Assert.Equal("chatcmpl-1", c.GetProperty("id").GetString()));

        JsonElement[] deltas = [.. chunks.Select(c => c.GetProperty("choices")[0].GetProperty("delta"))];
        Assert.Equal("assistant", deltas[0].GetProperty("role").GetString());
        Assert.Contains(deltas, d => d.TryGetProperty("reasoning_content", out JsonElement r) && r.GetString() == "想想");
        Assert.Contains(deltas, d => d.TryGetProperty("content", out JsonElement t) && t.GetString() == "你好");

        JsonElement toolStart = deltas.First(d => d.TryGetProperty("tool_calls", out _)).GetProperty("tool_calls")[0];
        Assert.Equal(0, toolStart.GetProperty("index").GetInt32());
        Assert.Equal("toolu_1", toolStart.GetProperty("id").GetString());
        Assert.Equal("get", toolStart.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains(
            deltas,
            d => d.TryGetProperty("tool_calls", out JsonElement calls)
                && calls[0].GetProperty("function").TryGetProperty("arguments", out JsonElement args)
                && args.GetString() == "{\"city\":");

        // 签名不外传；不要用量就没有用量块。
        Assert.DoesNotContain("sig", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, c => c.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object);
        Assert.Equal(
            "stop",
            chunks.Last().GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void AUsageChunkComesOnlyWhenTheClientAskedForIt()
    {
        string sse = Translate(true, MessageStart, TextStart, TextDelta, BlockStop, MessageDelta, MessageStop);

        JsonElement usage = Chunks(sse).Last().GetProperty("usage");
        Assert.Equal(20 + 5 + 7, usage.GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(9, usage.GetProperty("completion_tokens").GetInt64());
        Assert.Equal(5, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt64());
    }

    [Fact]
    public void TheIrIsWrittenBackAsAnAnthropicRequestWithThinkingOnlyWhenSigned()
    {
        byte[] body = AnthropicRequestWriter.Write(
            new Ir.IrRequest
            {
                Model = "m",
                MaxOutputTokens = 100,
                Stream = true,
                Messages =
                [
                    new Ir.IrMessage(Ir.IrRole.User, [new Ir.IrText("hi"), new Ir.IrText("")]),
                    new Ir.IrMessage(
                        Ir.IrRole.Assistant,
                        [new Ir.IrReasoning("unsigned", null), new Ir.IrReasoning("signed", "sig"), new Ir.IrText("ok")]),
                    new Ir.IrMessage(Ir.IrRole.User, [new Ir.IrText("")]),
                ],
            },
            "claude-real");

        JsonElement root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(100, root.GetProperty("max_tokens").GetInt64());
        Assert.True(root.GetProperty("stream").GetBoolean());

        // 空文本块与空消息都写不进 Anthropic（它会拒），没有签名的思考也不能回传。
        JsonElement[] messages = [.. root.GetProperty("messages").EnumerateArray()];
        Assert.Equal(2, messages.Length);
        Assert.Single(messages[0].GetProperty("content").EnumerateArray());
        JsonElement[] assistant = [.. messages[1].GetProperty("content").EnumerateArray()];
        Assert.Equal(["thinking", "text"], assistant.Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig", assistant[0].GetProperty("signature").GetString());
    }
}
