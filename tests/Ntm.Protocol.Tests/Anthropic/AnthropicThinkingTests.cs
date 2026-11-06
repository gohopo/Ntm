using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;

namespace Ntm.Protocol.Tests.Anthropic;

/// <summary>推理强度和思考预算的互转（Q288–Q299，ADR-0021）。</summary>
public sealed class AnthropicThinkingTests
{
    private const string Messages = "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]";

    private static JsonElement ToAnthropic(string chatJson, string upstreamModel = "claude-sonnet-4-5", AnthropicThinkingStyle style = AnthropicThinkingStyle.Auto)
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(chatJson));
        Assert.NotNull(conversion.Request);
        return JsonDocument.Parse(AnthropicRequestWriter.Write(conversion.Request, upstreamModel, false, style)).RootElement.Clone();
    }

    private static IrRequest ReadAnthropic(string json)
    {
        AnthropicRequestReading reading = AnthropicRequestReader.Read(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(reading.Request);
        return reading.Request;
    }

    private static string? Text(JsonElement root, string a, string? b = null)
    {
        if (!root.TryGetProperty(a, out JsonElement x))
        {
            return null;
        }

        if (b is null)
        {
            return x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText();
        }

        return x.TryGetProperty(b, out JsonElement y) ? (y.ValueKind == JsonValueKind.String ? y.GetString() : y.GetRawText()) : null;
    }

    [Theory]
    [InlineData("claude-opus-4-5-20251101", 4, 5)]
    [InlineData("claude-sonnet-4-20250514", 4, 0)]
    [InlineData("claude-3-7-sonnet-20250219", 3, 7)]
    [InlineData("claude-opus-4.6", 4, 6)]
    [InlineData("anthropic/claude-sonnet-4-6", 4, 6)]
    [InlineData("Claude-Opus-4-7", 4, 7)]
    [InlineData("claude-sonnet-5", 5, 0)]
    [InlineData("opus-4-7", 4, 7)]
    [InlineData("anthropic.sonnet-5-5-v1", 5, 5)]
    [InlineData("bedrock/fable-5-1", 5, 1)]
    public void ReadsTheClaudeVersionFromTheModelName(string model, int major, int minor) =>
        Assert.Equal((major, minor), AnthropicThinking.ClaudeVersion(model));

    [Theory]
    [InlineData("claude-opus-4-5", AnthropicThinkingStyle.Budget)]
    [InlineData("claude-3-7-sonnet-latest", AnthropicThinkingStyle.Budget)]
    [InlineData("claude-sonnet-4-6", AnthropicThinkingStyle.Adaptive)]
    [InlineData("claude-opus-4-7", AnthropicThinkingStyle.Adaptive)]
    [InlineData("claude-haiku-5-5", AnthropicThinkingStyle.Adaptive)]
    [InlineData("deepseek-chat", AnthropicThinkingStyle.Budget)]
    [InlineData("kimi-k2", AnthropicThinkingStyle.Budget)]
    [InlineData("mythos-5", AnthropicThinkingStyle.Adaptive)]
    [InlineData("gpt-opus-x", AnthropicThinkingStyle.Budget)]
    public void AutoPicksTheStyleByClaudeVersionAndFallsBackToBudget(string model, AnthropicThinkingStyle expected) =>
        Assert.Equal(expected, AnthropicThinking.Resolve(AnthropicThinkingStyle.Auto, model));

    [Theory]
    [InlineData("minimal", 1024)]
    [InlineData("low", 2048)]
    [InlineData("medium", 8192)]
    [InlineData("high", 24576)]
    [InlineData("xhigh", 32768)]
    [InlineData("max", 32768)]
    public void BudgetStyleTurnsEffortIntoABudgetAndFillsMaxTokens(string effort, long budget)
    {
        JsonElement body = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"" + effort + "\",\"temperature\":0.7," + Messages + "}");

        Assert.Equal("enabled", Text(body, "thinking", "type"));
        Assert.Equal(budget, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
        Assert.Equal(budget + AnthropicThinking.BudgetReplyAllowance, body.GetProperty("max_tokens").GetInt64());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("output_config", out _));
    }

    [Fact]
    public void BudgetStyleCapsTheBudgetUnderTheClientsLimitAndNeverRaisesIt()
    {
        JsonElement capped = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\",\"max_completion_tokens\":6000," + Messages + "}");
        Assert.Equal(6000 - AnthropicThinking.AnswerReserve, capped.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
        Assert.Equal(6000, capped.GetProperty("max_tokens").GetInt64());

        JsonElement tooSmall = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\",\"max_tokens\":1500,\"temperature\":0.2," + Messages + "}");
        Assert.False(tooSmall.TryGetProperty("thinking", out _));
        Assert.Equal(1500, tooSmall.GetProperty("max_tokens").GetInt64());
        Assert.Equal(0.2, tooSmall.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(0.97, true)]
    [InlineData(0.5, false)]
    public void BudgetStyleKeepsTopPOnlyInsideTheThinkingRange(double topP, bool kept)
    {
        JsonElement body = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"low\",\"top_p\":" + topP.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + Messages + "}");
        Assert.Equal(kept, body.TryGetProperty("top_p", out _));
    }

    [Fact]
    public void BudgetStyleWritesNothingForNoneOrNoEffort()
    {
        JsonElement none = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"none\",\"temperature\":0.3," + Messages + "}");
        Assert.False(none.TryGetProperty("thinking", out _));
        Assert.Equal(0.3, none.GetProperty("temperature").GetDouble());
        Assert.False(none.TryGetProperty("max_tokens", out _));

        JsonElement plain = ToAnthropic("{\"model\":\"m\"," + Messages + "}");
        Assert.False(plain.TryGetProperty("thinking", out _));
        Assert.False(plain.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    [InlineData("minimal", "low")]
    [InlineData("MEDIUM", "medium")]
    public void AdaptiveStyleWritesEffortAndDropsSampling(string effort, string written)
    {
        JsonElement body = ToAnthropic(
            "{\"model\":\"m\",\"reasoning_effort\":\"" + effort + "\",\"temperature\":1,\"top_p\":0.9," + Messages + "}",
            "claude-opus-4-7");

        Assert.Equal("adaptive", Text(body, "thinking", "type"));
        Assert.False(body.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        Assert.Equal(written, Text(body, "output_config", "effort"));
        Assert.Equal(AnthropicThinking.AdaptiveDefaultMaxTokens, body.GetProperty("max_tokens").GetInt64());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("top_p", out _));
    }

    [Fact]
    public void AdaptiveStyleTurnsNoneIntoLowEffortAndAlwaysDropsSampling()
    {
        JsonElement none = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"none\",\"temperature\":0.5,\"max_tokens\":800," + Messages + "}", "m", AnthropicThinkingStyle.Adaptive);
        Assert.False(none.TryGetProperty("thinking", out _));
        Assert.Equal("low", Text(none, "output_config", "effort"));
        Assert.Equal(800, none.GetProperty("max_tokens").GetInt64());
        Assert.False(none.TryGetProperty("temperature", out _));

        JsonElement plain = ToAnthropic("{\"model\":\"m\",\"temperature\":0.5," + Messages + "}", "claude-sonnet-4-6");
        Assert.False(plain.TryGetProperty("thinking", out _));
        Assert.False(plain.TryGetProperty("temperature", out _));
        Assert.False(plain.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void FixedStylesIgnoreTheModelName()
    {
        JsonElement budget = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"medium\"," + Messages + "}", "claude-opus-4-7", AnthropicThinkingStyle.Budget);
        Assert.Equal("enabled", Text(budget, "thinking", "type"));

        JsonElement adaptive = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"medium\"," + Messages + "}", "deepseek-chat", AnthropicThinkingStyle.Adaptive);
        Assert.Equal("adaptive", Text(adaptive, "thinking", "type"));
    }

    [Fact]
    public void UnknownEffortIsIgnoredForAnthropicButCopiedForOpenAi()
    {
        JsonElement anthropic = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"turbo\"," + Messages + "}");
        Assert.False(anthropic.TryGetProperty("thinking", out _));

        OpenAiChatRequestConversion chat = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"reasoning_effort\":\"turbo\"," + Messages + "}"));
        JsonElement responses = JsonDocument.Parse(ResponsesRequestWriter.Write(chat.Request!, "m", null)).RootElement;
        Assert.Equal("turbo", Text(responses, "reasoning", "effort"));
    }

    [Fact]
    public void ReadsAnthropicThinkingIntoTheIr()
    {
        IrRequest enabled = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":9000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":5000}," + Messages + "}");
        Assert.Equal(5000, enabled.ThinkingBudget);
        Assert.Null(enabled.ReasoningEffort);
        Assert.False(enabled.ThinkingDisabled);

        IrRequest adaptive = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":9000,\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"xhigh\"}," + Messages + "}");
        Assert.Equal("xhigh", adaptive.ReasoningEffort);
        Assert.Null(adaptive.ThinkingBudget);

        IrRequest both = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":9000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":3000},\"output_config\":{\"effort\":\"high\"}," + Messages + "}");
        Assert.Equal("high", both.ReasoningEffort);
        Assert.Equal(3000, both.ThinkingBudget);

        Assert.True(ReadAnthropic("{\"model\":\"m\",\"max_tokens\":10,\"thinking\":{\"type\":\"disabled\"}," + Messages + "}").ThinkingDisabled);
        Assert.True(ReadAnthropic("{\"model\":\"m\",\"max_tokens\":10,\"thinking\":{\"type\":\"between_tools\"}," + Messages + "}").ThinkingDisabled);
        Assert.False(ReadAnthropic("{\"model\":\"m\",\"max_tokens\":10,\"thinking\":\"weird\"," + Messages + "}").ThinkingDisabled);
    }

    [Theory]
    [InlineData(1024, "low")]
    [InlineData(2048, "low")]
    [InlineData(4096, "low")]
    [InlineData(4097, "medium")]
    [InlineData(8192, "medium")]
    [InlineData(16384, "medium")]
    [InlineData(24576, "high")]
    [InlineData(64000, "high")]
    public void BudgetsTurnIntoThreeEffortTiers(long budget, string effort) =>
        Assert.Equal(effort, AnthropicThinking.EffortForBudget(budget));

    [Fact]
    public void ForwardTableRoundTripsLowMediumHigh()
    {
        foreach (string effort in new[] { "low", "medium", "high" })
        {
            Assert.Equal(effort, AnthropicThinking.EffortForBudget(AnthropicThinking.BudgetFor(effort)!.Value));
        }
    }

    [Fact]
    public void AnthropicClientsBudgetBecomesOpenAiEffort()
    {
        IrRequest budget = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":30000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":10000}," + Messages + "}");
        JsonElement chat = JsonDocument.Parse(OpenAiChatRequestWriter.Write(budget, "gpt")).RootElement;
        Assert.Equal("medium", Text(chat, "reasoning_effort"));

        JsonElement responses = JsonDocument.Parse(ResponsesRequestWriter.Write(budget, "gpt", null)).RootElement;
        Assert.Equal("medium", Text(responses, "reasoning", "effort"));
        Assert.False(responses.GetProperty("reasoning").TryGetProperty("summary", out _));

        IrRequest effort = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":30000,\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"max\"}," + Messages + "}");
        Assert.Equal("max", Text(JsonDocument.Parse(OpenAiChatRequestWriter.Write(effort, "gpt")).RootElement, "reasoning_effort"));

        IrRequest disabled = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":100,\"thinking\":{\"type\":\"disabled\"}," + Messages + "}");
        Assert.False(JsonDocument.Parse(OpenAiChatRequestWriter.Write(disabled, "gpt")).RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void AnthropicClientsBudgetIsKeptExactlyOnBudgetStyleTargets()
    {
        IrRequest request = ReadAnthropic("{\"model\":\"m\",\"max_tokens\":30000,\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":10000}," + Messages + "}");
        JsonElement body = JsonDocument.Parse(AnthropicRequestWriter.Write(request, "deepseek-chat")).RootElement;

        Assert.Equal(10000, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
        Assert.Equal(30000, body.GetProperty("max_tokens").GetInt64());
    }

    [Fact]
    public void BudgetStyleDoesNotThinkWhenAToolIsForced()
    {
        // 预算写法下强制调用工具会被 Anthropic 拒绝（Q311）：不开思考。自适应照开。
        const string Forced = ",\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"f\",\"parameters\":{\"type\":\"object\"}}}],\"tool_choice\":";
        foreach (string choice in new[] { "\"required\"", "{\"type\":\"function\",\"function\":{\"name\":\"f\"}}" })
        {
            JsonElement budget = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"" + Forced + choice + "," + Messages + "}");
            Assert.False(budget.TryGetProperty("thinking", out _));

            JsonElement adaptive = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"" + Forced + choice + "," + Messages + "}", "claude-opus-4-7");
            Assert.Equal("adaptive", Text(adaptive, "thinking", "type"));
        }

        JsonElement auto = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"" + Forced + "\"auto\"," + Messages + "}");
        Assert.Equal("enabled", Text(auto, "thinking", "type"));
    }

    [Fact]
    public void BudgetStyleDoesNotThinkInsideAToolLoopWithoutSignedThinking()
    {
        // OpenAI 客户端回传不了签名，工具循环中途开思考会被拒（Q312）。
        const string Loop = "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"},"
            + "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]},"
            + "{\"role\":\"tool\",\"tool_call_id\":\"c1\",\"content\":\"42\"}]";
        JsonElement budget = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"," + Loop + "}");
        Assert.False(budget.TryGetProperty("thinking", out _));

        JsonElement adaptive = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"," + Loop + "}", "claude-sonnet-4-6");
        Assert.Equal("adaptive", Text(adaptive, "thinking", "type"));

        // 用户又说了一句，新的一轮照开。
        string fresh = Loop[..^1] + ",{\"role\":\"assistant\",\"content\":\"42\"},{\"role\":\"user\",\"content\":\"again\"}]";
        Assert.Equal("enabled", Text(ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"high\"," + fresh + "}"), "thinking", "type"));
    }

    [Fact]
    public void BudgetStyleKeepsThinkingInAToolLoopThatStartsWithSignedThinking()
    {
        IrRequest request = ReadAnthropic("""
            {"model":"m","max_tokens":30000,"thinking":{"type":"enabled","budget_tokens":4000},"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"plan","signature":"sig"},
                {"type":"tool_use","id":"t1","name":"f","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"42"}]}]}
            """);
        JsonElement body = JsonDocument.Parse(AnthropicRequestWriter.Write(request, "claude-sonnet-4-5")).RootElement;

        Assert.Equal("enabled", Text(body, "thinking", "type"));
        Assert.Equal(4000, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt64());
    }

    [Fact]
    public void AdaptiveThinkingAsksForSummarizedThinking()
    {
        // Claude 5 起默认不回思考文字（Q313）。
        JsonElement adaptive = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"medium\"," + Messages + "}", "claude-opus-5");
        Assert.Equal("summarized", Text(adaptive, "thinking", "display"));

        JsonElement budget = ToAnthropic("{\"model\":\"m\",\"reasoning_effort\":\"medium\"," + Messages + "}");
        Assert.False(budget.GetProperty("thinking").TryGetProperty("display", out _));
    }
}
