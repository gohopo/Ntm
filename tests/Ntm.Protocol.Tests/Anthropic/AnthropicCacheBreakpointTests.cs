using System.Text;
using System.Text.Json;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.Anthropic;

/// <summary>OpenAI 转 Anthropic 时的缓存断点（Q242–Q246，ADR-0019）。</summary>
public sealed class AnthropicCacheBreakpointTests
{
    private static JsonElement Convert(string json, bool auto = true, int reserved = 0)
    {
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(conversion.Request);
        byte[] body = AnthropicRequestWriter.Write(conversion.Request, "claude-up", auto, reservedBreakpoints: reserved);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Write(IrRequest request, bool auto = true) =>
        JsonDocument.Parse(AnthropicRequestWriter.Write(request, "claude-up", auto)).RootElement.Clone();

    /// <summary>所有带 cache_control 的位置，形如 tools[0]、system[1]、messages[2].content[0]，以及 ttl。</summary>
    private static List<string> Marks(JsonElement root)
    {
        List<string> marks = [];
        void Check(JsonElement node, string where)
        {
            if (node.TryGetProperty("cache_control", out JsonElement cache))
            {
                Assert.Equal("ephemeral", cache.GetProperty("type").GetString());
                marks.Add(cache.TryGetProperty("ttl", out JsonElement ttl) ? where + ":" + ttl.GetString() : where);
            }
        }

        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            int i = 0;
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                Check(tool, $"tools[{i++}]");
            }
        }

        if (root.TryGetProperty("system", out JsonElement system))
        {
            int i = 0;
            foreach (JsonElement part in system.EnumerateArray())
            {
                Check(part, $"system[{i++}]");
            }
        }

        int m = 0;
        foreach (JsonElement message in root.GetProperty("messages").EnumerateArray())
        {
            int b = 0;
            foreach (JsonElement block in message.GetProperty("content").EnumerateArray())
            {
                Check(block, $"messages[{m}].content[{b++}]");
            }

            m++;
        }

        return marks;
    }

    private const string Tools = """
        "tools":[
          {"type":"function","function":{"name":"a","parameters":{"type":"object"}}},
          {"type":"function","function":{"name":"b","parameters":{"type":"object"}}}]
        """;

    [Fact]
    public void AutoMarksTheLastToolTheLastSystemBlockTheLastMessageAndThePreviousUserMessage()
    {
        JsonElement root = Convert($$"""
            {"model":"m",{{Tools}},"messages":[
              {"role":"system","content":"规则一"},
              {"role":"system","content":"规则二"},
              {"role":"user","content":"第一问"},
              {"role":"assistant","content":"第一答"},
              {"role":"user","content":"第二问"}]}
            """);

        Assert.Equal(
            ["tools[1]", "system[1]", "messages[0].content[0]", "messages[2].content[0]"],
            Marks(root));
    }

    [Fact]
    public void ASingleTurnWithoutToolsOrSystemGetsOneMark()
    {
        JsonElement root = Convert("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(["messages[0].content[0]"], Marks(root));
    }

    [Fact]
    public void TheSwitchOffMeansNoAutoMarks()
    {
        JsonElement root = Convert(
            $$"""{"model":"m",{{Tools}},"messages":[{"role":"system","content":"s"},{"role":"user","content":"hi"}]}""",
            auto: false);

        Assert.Empty(Marks(root));
    }

    [Fact]
    public void ThinkingBlocksAreSkippedAndTheMarkMovesBack()
    {
        var request = new IrRequest
        {
            Model = "m",
            Messages =
            [
                new IrMessage(IrRole.User, [new IrText("问")]),
                new IrMessage(IrRole.Assistant,
                [
                    new IrText("先说"),
                    new IrReasoning("想", "sig", ApiFormat.Anthropic),
                    new IrRedactedReasoning("xx"),
                ]),
            ],
        };

        JsonElement root = Write(request);

        // 最后一条是 assistant：断点往前落到它的文字块；之前那条 user 消息也有一个。
        Assert.Equal(["messages[0].content[0]", "messages[1].content[0]"], Marks(root));
    }

    [Fact]
    public void ToolResultsAndToolUseCanCarryTheMark()
    {
        JsonElement root = Convert("""
            {"model":"m","messages":[
              {"role":"user","content":"查天气"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"get","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":"晴"}]}
            """);

        Assert.Equal(["messages[0].content[0]", "messages[2].content[0]"], Marks(root));
        JsonElement result = root.GetProperty("messages")[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
    }

    [Fact]
    public void ClientMarksAreUsedInsteadOfAutoMarksEvenWithTheSwitchOff()
    {
        string json = $$$"""
            {"model":"m",{{{Tools}}},"messages":[
              {"role":"system","content":[{"type":"text","text":"长规则","cache_control":{"type":"ephemeral","ttl":"1h"}}]},
              {"role":"user","content":[{"type":"text","text":"问","cache_control":{"type":"ephemeral"}}]},
              {"role":"assistant","content":"答"},
              {"role":"user","content":"再问"}]}
            """;

        Assert.Equal(["system[0]:1h", "messages[0].content[0]"], Marks(Convert(json, auto: true)));
        Assert.Equal(["system[0]:1h", "messages[0].content[0]"], Marks(Convert(json, auto: false)));
    }

    [Fact]
    public void MessageLevelAndToolLevelClientMarksAreRead()
    {
        JsonElement root = Convert("""
            {"model":"m",
             "tools":[{"type":"function","cache_control":{"type":"ephemeral"},"function":{"name":"a","parameters":{"type":"object"}}}],
             "messages":[
              {"role":"user","content":[{"type":"text","text":"一"},{"type":"text","text":"二"}],"cache_control":{"type":"ephemeral"}},
              {"role":"tool","tool_call_id":"c1","content":[{"type":"text","text":"结果","cache_control":{"type":"ephemeral"}}]}]}
            """);

        Assert.Equal(["tools[0]", "messages[0].content[1]", "messages[1].content[0]"], Marks(root));
    }

    [Fact]
    public void MoreThanFourMarksKeepTheLastToolSystemAndTwoMessageMarks()
    {
        const string Mark = """ "cache_control":{"type":"ephemeral"} """;
        JsonElement root = Convert("""
            {"model":"m",
             "tools":[
               {"type":"function",MARK,"function":{"name":"a","parameters":{"type":"object"}}},
               {"type":"function",MARK,"function":{"name":"b","parameters":{"type":"object"}}}],
             "messages":[
              {"role":"system","content":[{"type":"text","text":"s1",MARK},{"type":"text","text":"s2",MARK}]},
              {"role":"user","content":[{"type":"text","text":"u1",MARK}]},
              {"role":"assistant","content":[{"type":"text","text":"a1",MARK}]},
              {"role":"user","content":[{"type":"text","text":"u2",MARK}]}]}
            """.Replace("MARK", Mark, StringComparison.Ordinal));

        Assert.Equal(["tools[1]", "system[1]", "messages[1].content[0]", "messages[2].content[0]"], Marks(root));
    }

    [Fact]
    public void ARewriteRuleThatAddsATopLevelMarkLeavesRoomForIt()
    {
        const string Mark = """ "cache_control":{"type":"ephemeral"} """;
        const string Json = """
            {"model":"m",
             "tools":[{"type":"function",MARK,"function":{"name":"a","parameters":{"type":"object"}}}],
             "messages":[
              {"role":"system","content":[{"type":"text","text":"s1",MARK}]},
              {"role":"user","content":[{"type":"text","text":"u1",MARK}]},
              {"role":"assistant","content":[{"type":"text","text":"a1",MARK}]},
              {"role":"user","content":[{"type":"text","text":"u2",MARK}]}]}
            """;

        Assert.Equal(
            ["tools[0]", "system[0]", "messages[2].content[0]"],
            Marks(Convert(Json.Replace("MARK", Mark, StringComparison.Ordinal), reserved: 1)));

        // 自动断点也一样最多 3 个。
        JsonElement auto = Convert("""
            {"model":"m",
             "tools":[{"type":"function","function":{"name":"a","parameters":{"type":"object"}}}],
             "messages":[
              {"role":"system","content":"s"},
              {"role":"user","content":"u1"},
              {"role":"assistant","content":"a1"},
              {"role":"user","content":"u2"}]}
            """, reserved: 1);
        Assert.Equal(3, Marks(auto).Count);
        Assert.Contains("messages[2].content[0]", Marks(auto));

        // 规则值里还带了断点、让出 3 个时（Q316）：只剩最后一个消息断点；让出 4 个就一个也不放。
        Assert.Equal(
            ["messages[2].content[0]"],
            Marks(Convert(Json.Replace("MARK", Mark, StringComparison.Ordinal), reserved: 3)));
        Assert.Empty(Marks(Convert(Json.Replace("MARK", Mark, StringComparison.Ordinal), reserved: 4)));
    }

    [Fact]
    public void OneHourMarksAfterAFiveMinuteMarkBecomeFiveMinutes()
    {
        JsonElement root = Convert("""
            {"model":"m","messages":[
              {"role":"system","content":[{"type":"text","text":"s","cache_control":{"type":"ephemeral"}}]},
              {"role":"user","content":[{"type":"text","text":"u","cache_control":{"type":"ephemeral","ttl":"1h"}}]}]}
            """);

        Assert.Equal(["system[0]", "messages[0].content[0]"], Marks(root));
    }

    [Fact]
    public void ClientMarksOnBlocksThatAreNotWrittenOrNotEligibleAreDropped()
    {
        var request = new IrRequest
        {
            Model = "m",
            Messages =
            [
                new IrMessage(IrRole.User, [new IrText("问"), new IrText("") { Cache = IrCacheTtl.FiveMinutes }]),
                new IrMessage(IrRole.Assistant,
                [
                    new IrReasoning("想", "sig", ApiFormat.Anthropic) { Cache = IrCacheTtl.FiveMinutes },
                    new IrText("答"),
                ]),
            ],
        };

        // 客户端写了断点（虽然都落在不能放的位置上），网关就不再自动加。
        Assert.Empty(Marks(Write(request)));
    }

    [Fact]
    public void TheSameConversationAlwaysProducesTheSameBytes()
    {
        string json = $$"""{"model":"m",{{Tools}},"messages":[{"role":"system","content":"s"},{"role":"user","content":"hi"}]}""";
        OpenAiChatRequestConversion conversion = OpenAiChatRequestConverter.ToIr(Encoding.UTF8.GetBytes(json));

        Assert.Equal(
            AnthropicRequestWriter.Write(conversion.Request!, "c", autoCacheBreakpoints: true),
            AnthropicRequestWriter.Write(conversion.Request!, "c", autoCacheBreakpoints: true));
    }
}
