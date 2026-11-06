using System.Buffers;
using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;

namespace Ntm.Protocol.Tests.Anthropic;

/// <summary>OpenAI Chat 流式块 → Anthropic SSE 事件的状态机。</summary>
public sealed class OpenAiChatToAnthropicStreamTranslatorTests
{
    private const string MessageId = "msg_abc";

    private sealed record Event(string Name, JsonElement Data);

    private static List<Event> Parse(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        var events = new List<Event>();
        foreach (string frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] lines = frame.Split('\n');
            Assert.StartsWith("event: ", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("data: ", lines[1], StringComparison.Ordinal);
            string name = lines[0]["event: ".Length..];
            JsonElement data = JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone();

            // 事件名必须和 data 里的 type 一致，SDK 两边都会看。
            Assert.Equal(name, data.GetProperty("type").GetString());
            events.Add(new Event(name, data));
        }

        return events;
    }

    private static List<Event> Run(IEnumerable<string> chunks, Usage usage = default, string fallbackModel = "claude-x")
    {
        using var translator = new OpenAiChatToAnthropicStreamTranslator(MessageId, fallbackModel);
        var output = new ArrayBufferWriter<byte>();
        foreach (string chunk in chunks)
        {
            translator.Translate(Encoding.UTF8.GetBytes(chunk), output);
        }

        translator.Complete(usage, output);
        return Parse(output.WrittenSpan);
    }

    private static string[] Names(List<Event> events) => [.. events.Select(e => e.Name)];

    [Fact]
    public void PlainTextStream()
    {
        List<Event> events = Run(
            [
                """{"model":"gpt-4o-2024-11-20","choices":[{"index":0,"delta":{"role":"assistant"}}]}""",
                """{"choices":[{"index":0,"delta":{"content":"你"}}]}""",
                """{"choices":[{"index":0,"delta":{"content":"好"}}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
                """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":2}}""",
                "[DONE]",
            ],
            new Usage { InputTokens = 8, CacheReadTokens = 4, OutputTokens = 2, Source = UsageSource.Upstream });

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            Names(events));

        JsonElement message = events[0].Data.GetProperty("message");
        Assert.Equal(MessageId, message.GetProperty("id").GetString());
        Assert.Equal("gpt-4o-2024-11-20", message.GetProperty("model").GetString());
        Assert.Equal(0, message.GetProperty("content").GetArrayLength());

        Assert.Equal("text", events[1].Data.GetProperty("content_block").GetProperty("type").GetString());
        Assert.Equal("你", events[2].Data.GetProperty("delta").GetProperty("text").GetString());
        Assert.Equal("text_delta", events[3].Data.GetProperty("delta").GetProperty("type").GetString());

        JsonElement delta = events[5].Data;
        Assert.Equal("end_turn", delta.GetProperty("delta").GetProperty("stop_reason").GetString());
        JsonElement usage = delta.GetProperty("usage");
        Assert.Equal(8, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(4, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("output_tokens").GetInt64());
    }

    [Fact]
    public void ReasoningThenTextAreTwoBlocksInOrder()
    {
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"reasoning_content":"想"}}]}""",
            """{"choices":[{"delta":{"reasoning_content":"一下"}}]}""",
            """{"choices":[{"delta":{"content":"答"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
        ]);

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
             "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            Names(events));

        JsonElement thinking = events[1].Data.GetProperty("content_block");
        Assert.Equal("thinking", thinking.GetProperty("type").GetString());
        Assert.Equal(string.Empty, thinking.GetProperty("signature").GetString());
        Assert.Equal("thinking_delta", events[2].Data.GetProperty("delta").GetProperty("type").GetString());
        Assert.Equal(0, events[4].Data.GetProperty("index").GetInt32());
        Assert.Equal(1, events[5].Data.GetProperty("index").GetInt32());
    }

    [Fact]
    public void InterleavedParallelToolCallsLandOnTheirOwnBlocks()
    {
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"weather","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"time","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"tz\":\"UTC\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"上海\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
        ]);

        var starts = events.Where(e => e.Name == "content_block_start").ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal("call_a", starts[0].Data.GetProperty("content_block").GetProperty("id").GetString());
        Assert.Equal(0, starts[0].Data.GetProperty("index").GetInt32());
        Assert.Equal("call_b", starts[1].Data.GetProperty("content_block").GetProperty("id").GetString());
        Assert.Equal(1, starts[1].Data.GetProperty("index").GetInt32());

        // 把每个块的参数片段按块序号拼起来，交错到达也不能串块。
        var arguments = events
            .Where(e => e.Name == "content_block_delta")
            .GroupBy(e => e.Data.GetProperty("index").GetInt32())
            .ToDictionary(g => g.Key, g => string.Concat(g.Select(e => e.Data.GetProperty("delta").GetProperty("partial_json").GetString())));
        Assert.Equal("{\"city\":\"上海\"}", arguments[0]);
        Assert.Equal("{\"tz\":\"UTC\"}", arguments[1]);

        Assert.Equal(2, events.Count(e => e.Name == "content_block_stop"));
        Assert.Equal("tool_use", events.Single(e => e.Name == "message_delta").Data.GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void ToolCallsWithoutIndexAreSeparatedById()
    {
        // Gemini 的 OpenAI 兼容端点：每个并行调用整块发，不带 index。
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"tool_calls":[{"id":"g1","function":{"name":"a","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"id":"g2","function":{"name":"b","arguments":"{\"x\":1}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
        ]);

        var starts = events.Where(e => e.Name == "content_block_start").ToList();
        Assert.Equal(["g1", "g2"], starts.Select(s => s.Data.GetProperty("content_block").GetProperty("id").GetString()));
        Assert.Equal([0, 1], starts.Select(s => s.Data.GetProperty("index").GetInt32()));
    }

    [Fact]
    public void MissingToolIdGetsAStableSyntheticId()
    {
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"f","arguments":"{}"}}]}}]}""",
        ]);

        Assert.Equal("toolu_abc_0", events.Single(e => e.Name == "content_block_start").Data.GetProperty("content_block").GetProperty("id").GetString());
    }

    [Fact]
    public void ToolCallsReportedAsStopEndWithToolUse()
    {
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"f","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
        ]);

        Assert.Equal("tool_use", events.Single(e => e.Name == "message_delta").Data.GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void TextAfterToolCallsClosesThemFirst()
    {
        List<Event> events = Run(
        [
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"f","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"content":"然后"}}]}""",
        ]);

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_stop", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            Names(events));
        Assert.Equal(0, events[3].Data.GetProperty("index").GetInt32());
        Assert.Equal(1, events[4].Data.GetProperty("index").GetInt32());
    }

    [Fact]
    public void PreambleOnlyEmitsMessageStart()
    {
        using var translator = new OpenAiChatToAnthropicStreamTranslator(MessageId, "m");
        var output = new ArrayBufferWriter<byte>();

        translator.Translate("""{"choices":[{"delta":{"role":"assistant","content":""}}]}"""u8, output);

        Assert.True(translator.Started);
        Assert.Equal(["message_start"], Names(Parse(output.WrittenSpan)));
    }

    [Theory]
    [InlineData("[DONE]")]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""")]
    [InlineData("""{"error":{"message":"boom"}}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void FramesWithoutChoicesProduceNothing(string data)
    {
        using var translator = new OpenAiChatToAnthropicStreamTranslator(MessageId, "m");
        var output = new ArrayBufferWriter<byte>();

        translator.Translate(Encoding.UTF8.GetBytes(data), output);

        Assert.Equal(0, output.WrittenCount);
        Assert.False(translator.Started);
    }

    [Fact]
    public void CompleteWithoutAnyChunkStillProducesAValidMessage()
    {
        List<Event> events = Run([]);

        Assert.Equal(["message_start", "message_delta", "message_stop"], Names(events));
        Assert.Equal("claude-x", events[0].Data.GetProperty("message").GetProperty("model").GetString());
    }

    [Fact]
    public void CompleteIsIdempotentAndLaterChunksAreIgnored()
    {
        using var translator = new OpenAiChatToAnthropicStreamTranslator(MessageId, "m");
        var output = new ArrayBufferWriter<byte>();
        translator.Translate("""{"choices":[{"delta":{"content":"x"}}]}"""u8, output);
        translator.Complete(default, output);
        int written = output.WrittenCount;

        translator.Complete(default, output);
        translator.Translate("""{"choices":[{"delta":{"content":"y"}}]}"""u8, output);

        Assert.True(translator.Completed);
        Assert.Equal(written, output.WrittenCount);
    }

    [Fact]
    public void ErrorEventHasAnthropicShape()
    {
        var output = new ArrayBufferWriter<byte>();

        OpenAiChatToAnthropicStreamTranslator.WriteError(output, "overloaded_error", "上游过载");

        Event error = Assert.Single(Parse(output.WrittenSpan));
        Assert.Equal("error", error.Name);
        Assert.Equal("overloaded_error", error.Data.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("上游过载", error.Data.GetProperty("error").GetProperty("message").GetString());
    }
}
