using Ntm.Gateway.Configuration;
using Ntm.Gateway.Routing;

namespace Ntm.Gateway.Tests.Routing;

public class ModelPatternTests
{
    private static ModelPattern Create(MatchKind kind, string pattern)
    {
        Assert.True(ModelPattern.TryCreate(kind, pattern, out ModelPattern? compiled, out string? error), error);
        return compiled!;
    }

    [Fact]
    public void ExactNamesTreatDotsLiterally()
    {
        // AxonHub 的启发式会把 gpt-4.1 当正则，于是 gpt-4x1 也匹配上了（ADR-0011）。
        ModelPattern pattern = Create(MatchKind.Exact, "gpt-4.1");

        Assert.True(pattern.IsMatch("gpt-4.1"));
        Assert.False(pattern.IsMatch("gpt-4x1"));
    }

    [Fact]
    public void ExactNamesAreCaseSensitive()
    {
        ModelPattern pattern = Create(MatchKind.Exact, "gpt-4o");

        Assert.True(pattern.IsMatch("gpt-4o"));
        Assert.False(pattern.IsMatch("GPT-4O"));
    }

    [Fact]
    public void RegexIsFullyAnchored()
    {
        // 子串匹配会让 gpt-4 把 gpt-4o-mini-audio 也收进来。
        ModelPattern pattern = Create(MatchKind.Regex, "gpt-4");

        Assert.True(pattern.IsMatch("gpt-4"));
        Assert.False(pattern.IsMatch("gpt-4o"));
        Assert.False(pattern.IsMatch("azure/gpt-4"));
    }

    [Fact]
    public void RegexMatchesAWholeFamily()
    {
        ModelPattern pattern = Create(MatchKind.Regex, "gpt-4o.*");

        Assert.True(pattern.IsMatch("gpt-4o"));
        Assert.True(pattern.IsMatch("gpt-4o-mini"));
        Assert.False(pattern.IsMatch("gpt-4-turbo"));
    }

    [Fact]
    public void UserWrittenAnchorsAreAcceptedAndNormalized()
    {
        ModelPattern pattern = Create(MatchKind.Regex, "^claude-.*$");

        Assert.Equal("^(?:claude-.*)$", pattern.Effective);
        Assert.True(pattern.IsMatch("claude-sonnet-4-5"));
    }

    [Fact]
    public void AnInlineCaseInsensitiveModifierStaysOutsideTheAnchor()
    {
        ModelPattern pattern = Create(MatchKind.Regex, "(?i)gpt-4O");

        Assert.Equal("(?i)^(?:gpt-4O)$", pattern.Effective);
        Assert.True(pattern.IsMatch("gpt-4o"));
    }

    [Fact]
    public void RegexIsCaseSensitiveWithoutTheModifier() =>
        Assert.False(Create(MatchKind.Regex, "gpt-4o").IsMatch("GPT-4o"));

    [Fact]
    public void AnEscapedDollarIsNotTreatedAsAnAnchor()
    {
        // 模型名里真带 $ 的话，末尾的 \$ 不能被当成锚点吃掉。
        ModelPattern pattern = Create(MatchKind.Regex, @"price\$");

        Assert.Equal(@"^(?:price\$)$", pattern.Effective);
        Assert.True(pattern.IsMatch("price$"));
    }

    [Fact]
    public void CatastrophicBacktrackingCannotHappen()
    {
        // 回溯引擎会在这个模式上挂住；NonBacktracking 是线性的。
        ModelPattern pattern = Create(MatchKind.Regex, "(a+)+b");

        Assert.False(pattern.IsMatch(new string('a', 60)));
    }

    [Theory]
    [InlineData("(?=gpt)gpt-4")]
    [InlineData("(?<=azure/)gpt-4")]
    [InlineData(@"(gpt)-\1")]
    [InlineData("(?>gpt-4)")]
    public void UnsupportedConstructsAreRejectedWithAnExplanation(string pattern)
    {
        Assert.False(ModelPattern.TryCreate(MatchKind.Regex, pattern, out ModelPattern? compiled, out string? error));
        Assert.Null(compiled);
        Assert.Contains("排除模式", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASyntaxErrorIsReported()
    {
        Assert.False(ModelPattern.TryCreate(MatchKind.Regex, "gpt-4[", out _, out string? error));
        Assert.Contains("语法错误", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPatternIsRejected()
    {
        Assert.False(ModelPattern.TryCreate(MatchKind.Exact, string.Empty, out _, out string? error));
        Assert.Equal("模式不能为空。", error);
    }

    [Fact]
    public void AStarIsNotAGlob()
    {
        // 「gpt-*」在正则里是 gpt 后面跟零或多个连字符，不是通配符。显式选正则就按正则算。
        ModelPattern pattern = Create(MatchKind.Regex, "gpt-*");

        Assert.True(pattern.IsMatch("gpt"));
        Assert.False(pattern.IsMatch("gpt-4o"));
    }
}
