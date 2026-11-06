using System.Text;
using Ntm.Gateway.Routing;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Routing;

/// <summary>Conversation 的身份（Q118）：API 密钥 + 下发模型 + system + 第一条非 system 消息。</summary>
public class ConversationKeyTests
{
    private static UInt128? Key(string json, int? apiKeyId = 1, string model = "gpt-4o", ApiFormat format = ApiFormat.OpenAiChat) =>
        ConversationKey.Compute(format, Encoding.UTF8.GetBytes(json), apiKeyId, model);

    private const string FirstTurn =
        """{"model":"gpt-4o","messages":[{"role":"system","content":"s"},{"role":"user","content":"hi"}]}""";

    private const string LaterTurn =
        """{"model":"gpt-4o","stream":true,"messages":[{"role":"system","content":"s"},{"role":"user","content":"hi"},{"role":"assistant","content":"yo"},{"role":"user","content":"more"}]}""";

    [Fact]
    public void LaterTurnsOfTheSameConversationShareTheKey()
    {
        Assert.NotNull(Key(FirstTurn));
        Assert.Equal(Key(FirstTurn), Key(LaterTurn));
    }

    [Fact]
    public void TheApiKeyModelSystemAndFirstMessageAllMatter()
    {
        UInt128? baseline = Key(FirstTurn);
        Assert.NotEqual(baseline, Key(FirstTurn, apiKeyId: 2));
        Assert.NotEqual(baseline, Key(FirstTurn, apiKeyId: null));
        Assert.NotEqual(baseline, Key(FirstTurn, model: "other"));
        Assert.NotEqual(baseline, Key(FirstTurn.Replace("\"s\"", "\"t\"", StringComparison.Ordinal)));
        Assert.NotEqual(baseline, Key(FirstTurn.Replace("\"hi\"", "\"ho\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResponsesUsesInstructionsAndTheFirstInputItem()
    {
        const string first = """{"model":"m","instructions":"be brief","input":[{"role":"user","content":"hi"}]}""";
        const string later = """{"model":"m","instructions":"be brief","input":[{"role":"user","content":"hi"},{"type":"function_call","call_id":"c","name":"f","arguments":"{}"},{"role":"user","content":"more"}]}""";

        UInt128? key = Key(first, format: ApiFormat.OpenAiResponses);
        Assert.NotNull(key);
        Assert.Equal(key, Key(later, format: ApiFormat.OpenAiResponses));
        Assert.NotEqual(key, Key(first.Replace("be brief", "be long", StringComparison.Ordinal), format: ApiFormat.OpenAiResponses));
    }

    [Fact]
    public void AResponsesStringInputIsTheFirstMessage()
    {
        UInt128? key = Key("""{"model":"m","input":"hi"}""", format: ApiFormat.OpenAiResponses);

        Assert.NotNull(key);
        Assert.NotEqual(key, Key("""{"model":"m","input":"ho"}""", format: ApiFormat.OpenAiResponses));
        Assert.Null(Key("""{"model":"m","input":""}""", format: ApiFormat.OpenAiResponses));
    }

    [Fact]
    public void AnthropicUsesTheTopLevelSystem()
    {
        const string withSystem = """{"model":"m","system":"be brief","messages":[{"role":"user","content":"hi"}]}""";
        const string otherSystem = """{"model":"m","system":"be long","messages":[{"role":"user","content":"hi"}]}""";

        Assert.NotEqual(Key(withSystem, format: ApiFormat.Anthropic), Key(otherSystem, format: ApiFormat.Anthropic));
    }

    [Fact]
    public void WithoutAConversationThereIsNoKey()
    {
        Assert.Null(Key("""{"model":"gpt-4o","messages":[]}"""));
        Assert.Null(Key("""{"model":"gpt-4o","messages":[{"role":"system","content":"s"}]}"""));
        Assert.Null(Key("""{"model":"gpt-4o"}"""));
        Assert.Null(Key("not json"));
    }
}
