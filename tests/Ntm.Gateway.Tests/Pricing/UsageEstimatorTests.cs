using System.Text;
using Ntm.Gateway.Pricing;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>上游没给用量时的字符估算（Q47）。</summary>
public sealed class UsageEstimatorTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void AsciiInputCountsFourCharsPerToken()
    {
        // 字符串字段合起来 16 个 ASCII 字符 → 4 Token。
        byte[] body = Body("""{"model":"gpt-4.1","messages":[{"role":"user","content":"abc"}]}""");

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 40, outputWideChars: 0);

        // "gpt-4.1"(7) + "user"(4) + "abc"(3) = 14 → 向上取整 4；输出 40/4 = 10。
        Assert.Equal(4, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(UsageSource.GatewayEstimated, usage.Source);
    }

    [Fact]
    public void WideCharsCountOnePerToken()
    {
        byte[] body = Body("""{"content":"你好世界"}""");

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 0, outputWideChars: 12);

        // "content" 不算（键不是字符串值），值里 4 个汉字 → 4 Token。
        Assert.Equal(4, usage.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
    }

    [Fact]
    public void EverythingCountsAsNonCachedInput()
    {
        byte[] body = Body("""{"content":"hello"}""");

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 0, outputWideChars: 0);

        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWrite5mTokens);
        Assert.Equal(0, usage.CacheWrite1hTokens);
        Assert.True(usage.InputTokens > 0);
    }

    [Fact]
    public void ALongBase64FieldCountsAsOneImage()
    {
        string data = new('A', 4000);
        byte[] body = Body(
            "{\"messages\":[{\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,"
            + data
            + "\"}}]}]}");

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 0, outputWideChars: 0);

        // 4000 个字符的 base64 不按 1000 Token 算，而是按一张图 1000 Token，
        // 再加上几个短字段（"image_url" 等）。
        Assert.InRange(usage.InputTokens, UsageEstimator.TokensPerImage, UsageEstimator.TokensPerImage + 20);
    }

    [Fact]
    public void ALongProseFieldIsStillCountedAsText()
    {
        // 长但有空格和标点，不是 base64。
        string prose = string.Join(' ', Enumerable.Repeat("the quick brown fox.", 40));
        byte[] body = Body($$"""{"content":"{{prose}}"}""");

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 0, outputWideChars: 0);

        Assert.Equal((prose.Length + 3) / 4, usage.InputTokens);
    }

    [Fact]
    public void ABodyThatIsNotJsonFallsBackToByteLength()
    {
        byte[] body = Encoding.UTF8.GetBytes(new string('x', 400));

        Usage usage = UsageEstimator.Estimate(body, outputAsciiChars: 0, outputWideChars: 0);

        Assert.Equal(100, usage.InputTokens);
    }

    [Fact]
    public void AnEmptyBodyEstimatesNothing()
    {
        Usage usage = UsageEstimator.Estimate([], outputAsciiChars: 0, outputWideChars: 0);

        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(UsageSource.GatewayEstimated, usage.Source);
    }
}
