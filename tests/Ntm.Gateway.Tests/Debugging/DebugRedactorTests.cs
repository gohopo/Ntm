using Ntm.Gateway.Debugging;

namespace Ntm.Gateway.Tests.Debugging;

/// <summary>请求头脱敏（Q158）：认证头只留前缀，值里出现已知密钥的也要换掉，正文不管。</summary>
public class DebugRedactorTests
{
    [Fact]
    public void AnAuthorizationHeaderKeepsTheSchemeAndAFewCharacters()
    {
        Assert.Equal("Bearer sk-abc***", DebugRedactor.Redact("Authorization", "Bearer sk-abcdefghijklmn"));
    }

    [Fact]
    public void AnApiKeyHeaderIsRedactedWholeAndTheNameIsCaseInsensitive()
    {
        Assert.Equal("sk-ant***", DebugRedactor.Redact("X-Api-Key", "sk-ant-0123456789"));
        Assert.Equal("***", DebugRedactor.Redact("api-key", "short"));
    }

    [Fact]
    public void CookiesAreRedactedToo()
    {
        Assert.Equal("sessio***", DebugRedactor.Redact("Cookie", "session=abcdefghij"));
    }

    [Fact]
    public void AKnownSecretIsReplacedEvenInAnUnexpectedHeader()
    {
        string result = DebugRedactor.Redact("x-custom-auth", "key=sk-upstream-9999 end", ["sk-upstream-9999"]);

        Assert.Equal("key=sk-ups*** end", result);
    }

    [Fact]
    public void AShortSecretIsNotHuntedForInOtherHeaders()
    {
        // 太短的串在普通文本里到处都是，照着换会把正常内容毁掉。
        Assert.Equal("model=gpt-4o", DebugRedactor.Redact("x-note", "model=gpt-4o", ["gpt"]));
    }

    [Fact]
    public void OrdinaryHeadersGoThroughUntouched()
    {
        Assert.Equal("application/json", DebugRedactor.Redact("Content-Type", "application/json"));
    }

    [Fact]
    public void FormatWritesOneLinePerValue()
    {
        string text = DebugRedactor.Format(
            [
                new("Authorization", ["Bearer sk-abcdefgh"]),
                new("Accept", ["application/json"]),
            ]);

        Assert.Equal("Authorization: Bearer sk-abc***\nAccept: application/json\n", text);
    }
}
