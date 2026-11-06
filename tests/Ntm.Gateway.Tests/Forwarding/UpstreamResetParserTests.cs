using System.Globalization;
using Ntm.Gateway.Forwarding;

namespace Ntm.Gateway.Tests.Forwarding;

public class UpstreamResetParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset? Parse(params (string Name, string Value)[] headers)
    {
        using var response = new HttpResponseMessage();
        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return UpstreamResetParser.Parse(response.Headers, Now);
    }

    [Fact]
    public void NoHeadersMeansNoResetTime() => Assert.Null(Parse());

    [Fact]
    public void RetryAfterSecondsIsUsed() =>
        Assert.Equal(Now.AddSeconds(42), Parse(("Retry-After", "42")));

    [Fact]
    public void RetryAfterHttpDateIsUsed() =>
        Assert.Equal(
            Now.AddMinutes(5),
            Parse(("Retry-After", Now.AddMinutes(5).ToString("R", CultureInfo.InvariantCulture))));

    [Fact]
    public void MillisecondHeaderWinsOverSeconds()
    {
        // retry-after-ms 更精确；同时存在时不该把 1.5 秒当成 2 秒。
        DateTimeOffset? at = Parse(("retry-after-ms", "1500"), ("Retry-After", "2"));

        Assert.Equal(Now.AddMilliseconds(1500), at);
    }

    [Fact]
    public void OpenAiResetDurationIsParsed() =>
        Assert.Equal(
            Now.AddSeconds(360),
            Parse(("x-ratelimit-remaining-requests", "0"), ("x-ratelimit-reset-requests", "6m0s")));

    [Fact]
    public void AnthropicRfc3339ResetIsParsed() =>
        Assert.Equal(
            Now.AddSeconds(90),
            Parse(
                ("anthropic-ratelimit-requests-remaining", "0"),
                ("anthropic-ratelimit-requests-reset", Now.AddSeconds(90).ToString("O", CultureInfo.InvariantCulture))));

    [Fact]
    public void ADimensionThatStillHasHeadroomIsIgnored()
    {
        // 令牌数耗尽了，请求数还有余量：用令牌数的重置时间，否则会白等。
        DateTimeOffset? at = Parse(
            ("x-ratelimit-remaining-requests", "58"),
            ("x-ratelimit-reset-requests", "8m0s"),
            ("x-ratelimit-remaining-tokens", "0"),
            ("x-ratelimit-reset-tokens", "30s"));

        Assert.Equal(Now.AddSeconds(30), at);
    }

    [Fact]
    public void WithoutRemainingCountsTheLaterResetWins()
    {
        DateTimeOffset? at = Parse(("x-ratelimit-reset-requests", "10s"), ("x-ratelimit-reset-tokens", "45s"));

        Assert.Equal(Now.AddSeconds(45), at);
    }

    [Fact]
    public void ZeroIsLiftedToAMinimumDelay()
    {
        // 上游报 0 时立刻再撞一次没有意义。
        DateTimeOffset? at = Parse(("Retry-After", "0"));

        Assert.Equal(Now.AddSeconds(1), at);
    }

    [Fact]
    public void APastDateIsLiftedToAMinimumDelay() =>
        Assert.Equal(
            Now.AddSeconds(1),
            Parse(("Retry-After", Now.AddMinutes(-5).ToString("R", CultureInfo.InvariantCulture))));

    [Fact]
    public void AnAbsurdlyLongResetIsCappedAt24Hours()
    {
        // 按月配额的上游可能给出很远的时间，但再远也不该让候选消失一整周。
        DateTimeOffset? at = Parse(("Retry-After", "604800"));

        Assert.Equal(Now.AddHours(24), at);
    }

    [Fact]
    public void GarbageIsIgnored() => Assert.Null(Parse(("Retry-After", "soon"), ("retry-after-ms", "later")));

    [Theory]
    [InlineData("6m0s", 360)]
    [InlineData("1.5s", 1.5)]
    [InlineData("120ms", 0.12)]
    [InlineData("1h30m", 5400)]
    [InlineData("2d", 172800)]
    [InlineData("45", 45)]
    [InlineData("0s", 0)]
    public void DurationStringsAreParsed(string text, double expectedSeconds)
    {
        Assert.True(UpstreamResetParser.TryDuration(text, out TimeSpan value));
        Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("6x0s")]
    [InlineData("m6")]
    public void BadDurationStringsAreRejected(string text) =>
        Assert.False(UpstreamResetParser.TryDuration(text, out _));
}
