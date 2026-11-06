using System.Net.Http;
using System.Text;
using Ntm.Gateway.Rewrite;

namespace Ntm.Gateway.Tests.Rewrite;

public class RequestRewriterTests
{
    private static ChannelRewriteRule Body(RewriteOperation op, string path, string? value = null) =>
        new() { Target = RewriteTarget.Body, Operation = op, Path = path, Value = value };

    private static ChannelRewriteRule Header(RewriteOperation op, string name, string? value = null) =>
        new() { Target = RewriteTarget.Header, Operation = op, Path = name, Value = value };

    private static string Apply(string json, params ChannelRewriteRule[] rules) =>
        Encoding.UTF8.GetString(RequestRewriter.From(rules).ApplyToBody(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void SetWritesANestedValueAndCreatesTheObjectsOnTheWay()
    {
        string result = Apply(
            """{"messages":[{"role":"user"}]}""",
            Body(RewriteOperation.Set, "thinking.type", "\"disabled\""));

        Assert.Equal("""{"messages":[{"role":"user"}],"thinking":{"type":"disabled"}}""", result);
    }

    [Fact]
    public void SetOverwritesWhatTheClientSent()
    {
        string result = Apply("""{"temperature":0.9}""", Body(RewriteOperation.Set, "temperature", "0.2"));

        Assert.Equal("""{"temperature":0.2}""", result);
    }

    [Fact]
    public void DefaultOnlyWritesWhenTheClientDidNotSendIt()
    {
        ChannelRewriteRule rule = Body(RewriteOperation.Default, "temperature", "0.2");

        Assert.Equal("""{"temperature":0.9}""", Apply("""{"temperature":0.9}""", rule));
        Assert.Equal("""{"model":"x","temperature":0.2}""", Apply("""{"model":"x"}""", rule));
    }

    [Fact]
    public void DefaultTreatsAnExplicitNullAsSent()
    {
        // 客户端明写 null 是一种选择，不该被补成别的值。
        Assert.Equal("""{"temperature":null}""", Apply("""{"temperature":null}""", Body(RewriteOperation.Default, "temperature", "0.2")));
    }

    [Fact]
    public void RemoveDropsTheFieldAndIsQuietWhenItIsNotThere()
    {
        Assert.Equal("""{"model":"x"}""", Apply("""{"model":"x","top_k":5}""", Body(RewriteOperation.Remove, "top_k")));
        Assert.Equal("""{"model":"x"}""", Apply("""{"model":"x"}""", Body(RewriteOperation.Remove, "top_k")));
        Assert.Equal("""{"a":{}}""", Apply("""{"a":{"b":1}}""", Body(RewriteOperation.Remove, "a.b")));
    }

    [Fact]
    public void ARuleWhoseIntermediateIsNotAnObjectIsSkipped()
    {
        // 宁可这条规则不生效，也不毁掉客户端给的值。
        Assert.Equal("""{"thinking":"on"}""", Apply("""{"thinking":"on"}""", Body(RewriteOperation.Set, "thinking.type", "\"disabled\"")));
    }

    [Fact]
    public void RulesRunInOrder()
    {
        string result = Apply(
            """{"a":1}""",
            Body(RewriteOperation.Set, "a", "2"),
            Body(RewriteOperation.Set, "a", "3"));

        Assert.Equal("""{"a":3}""", result);
    }

    [Fact]
    public void ObjectsAndArraysCanBeWrittenWhole()
    {
        string result = Apply(
            "{}",
            Body(RewriteOperation.Set, "thinking", """{"type":"enabled","budget_tokens":1024}"""),
            Body(RewriteOperation.Set, "stop", """["\n\n"]"""));

        Assert.Equal("""{"thinking":{"type":"enabled","budget_tokens":1024},"stop":["\n\n"]}""", result);
    }

    [Fact]
    public void NonAsciiTextStaysReadableAndNumbersKeepTheirForm()
    {
        string result = Apply("""{"prompt":"你好","temperature":1.0}""", Body(RewriteOperation.Set, "top_p", "0.95"));

        Assert.Equal("""{"prompt":"你好","temperature":1.0,"top_p":0.95}""", result);
    }

    [Fact]
    public void ABodyThatIsNotAJsonObjectIsLeftAlone()
    {
        ChannelRewriteRule rule = Body(RewriteOperation.Set, "a", "1");

        Assert.Equal("[1,2]", Apply("[1,2]", rule));
        Assert.Equal("not json", Apply("not json", rule));
    }

    [Fact]
    public void WithoutRulesTheSameArrayInstanceComesBack()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"a":1}""");

        Assert.Same(body, RequestRewriter.Empty.ApplyToBody(body));
        Assert.Same(body, RequestRewriter.From([Header(RewriteOperation.Set, "x-a", "1")]).ApplyToBody(body));
    }

    [Fact]
    public void HeaderRulesSetRemoveAndDefault()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://upstream.test/v1/chat/completions");
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        message.Headers.TryAddWithoutValidation("x-keep", "1");

        RequestRewriter.From([
            Header(RewriteOperation.Set, "anthropic-version", "2024-10-22"),
            Header(RewriteOperation.Remove, "x-keep"),
            Header(RewriteOperation.Default, "x-new", "fresh"),
            Header(RewriteOperation.Default, "anthropic-version", "ignored"),
        ]).ApplyToHeaders(message.Headers);

        Assert.Equal("2024-10-22", Assert.Single(message.Headers.GetValues("anthropic-version")));
        Assert.False(message.Headers.Contains("x-keep"));
        Assert.Equal("fresh", Assert.Single(message.Headers.GetValues("x-new")));
    }

    [Theory]
    [InlineData(RewriteTarget.Header, "Authorization", "\"x\"")]
    [InlineData(RewriteTarget.Header, "x-api-key", "\"x\"")]
    [InlineData(RewriteTarget.Header, "Content-Type", "\"x\"")]
    [InlineData(RewriteTarget.Body, "model", "\"x\"")]
    [InlineData(RewriteTarget.Body, "stream", "true")]
    public void ProtectedPlacesAreRejected(RewriteTarget target, string path, string value)
    {
        (string Field, string Message)? problem = RequestRewriter.Validate(
            new ChannelRewriteRule { Target = target, Operation = RewriteOperation.Set, Path = path, Value = value });

        Assert.Equal("path", problem?.Field);
        Assert.Contains("不能改写", problem?.Message);
    }

    [Fact]
    public void ProtectedNamesAreRejectedForRemoveToo()
    {
        Assert.NotNull(RequestRewriter.Validate(
            new ChannelRewriteRule { Target = RewriteTarget.Body, Operation = RewriteOperation.Remove, Path = "stream" }));
    }

    [Fact]
    public void ANestedFieldUnderAProtectedNameIsFine()
    {
        // 挡的是顶层的 model 和 stream；stream_options 之类的名字不受影响。
        Assert.Null(RequestRewriter.Validate(
            new ChannelRewriteRule { Target = RewriteTarget.Body, Operation = RewriteOperation.Set, Path = "stream_options.foo", Value = "1" }));
    }

    [Theory]
    [InlineData("", "path")]
    [InlineData("a..b", "path")]
    [InlineData(".a", "path")]
    public void BadBodyPathsAreRejected(string path, string field)
    {
        Assert.Equal(field, RequestRewriter.Validate(
            new ChannelRewriteRule { Target = RewriteTarget.Body, Operation = RewriteOperation.Set, Path = path, Value = "1" })?.Field);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("{")]
    [InlineData("")]
    public void ABodyValueMustBeAJsonLiteral(string value)
    {
        Assert.Equal("value", RequestRewriter.Validate(
            new ChannelRewriteRule { Target = RewriteTarget.Body, Operation = RewriteOperation.Set, Path = "thinking", Value = value })?.Field);
    }

    [Fact]
    public void AHeaderValueMayBePlainTextButNotMultiLine()
    {
        Assert.Null(RequestRewriter.Validate(Header(RewriteOperation.Set, "x-a", "plain text")));
        Assert.Equal("value", RequestRewriter.Validate(Header(RewriteOperation.Set, "x-a", "a\r\nb"))?.Field);
    }

    [Fact]
    public void AHeaderNameMustBeAToken()
    {
        Assert.Equal("path", RequestRewriter.Validate(Header(RewriteOperation.Set, "bad header", "1"))?.Field);
    }

    [Fact]
    public void TooLongPathsAndValuesAreRejected()
    {
        Assert.Equal("path", RequestRewriter.Validate(
            new ChannelRewriteRule { Target = RewriteTarget.Body, Operation = RewriteOperation.Remove, Path = new string('a', 201) })?.Field);
        Assert.Equal("value", RequestRewriter.Validate(
            new ChannelRewriteRule
            {
                Target = RewriteTarget.Body,
                Operation = RewriteOperation.Set,
                Path = "a",
                Value = "\"" + new string('b', 2000) + "\"",
            })?.Field);
    }

    [Fact]
    public void FromSkipsRulesThatWouldNotValidate()
    {
        // 库被手改过的兜底：坏规则跳过，好规则照用。
        RequestRewriter rewriter = RequestRewriter.From([
            Body(RewriteOperation.Set, "model", "\"sneaky\""),
            Body(RewriteOperation.Set, "top_p", "0.5"),
        ]);

        Assert.Equal("""{"model":"x","top_p":0.5}""", Encoding.UTF8.GetString(rewriter.ApplyToBody(Encoding.UTF8.GetBytes("""{"model":"x"}"""))));
    }

    [Fact]
    public void BreakpointsWrittenByRulesAreCounted()
    {
        Assert.Equal(1, RequestRewriter.From([Body(RewriteOperation.Set, "cache_control", """{"type":"ephemeral"}""")]).CacheBreakpointsWritten);
        Assert.Equal(1, RequestRewriter.From([Body(RewriteOperation.Default, "cache_control", """{"type":"ephemeral"}""")]).CacheBreakpointsWritten);
        Assert.Equal(0, RequestRewriter.From([Body(RewriteOperation.Remove, "cache_control")]).CacheBreakpointsWritten);
        Assert.Equal(0, RequestRewriter.From([Body(RewriteOperation.Set, "metadata.cache_control", "1")]).CacheBreakpointsWritten);
        Assert.Equal(0, RequestRewriter.From([Header(RewriteOperation.Set, "cache_control", "1")]).CacheBreakpointsWritten);

        // 规则值里带的断点也算（Q316）：一个 system 数组里两个，加一个顶层的。
        RequestRewriter rewriter = RequestRewriter.From([
            Body(RewriteOperation.Set, "system", """[{"type":"text","text":"a","cache_control":{"type":"ephemeral"}},{"type":"text","text":"b","cache_control":{"type":"ephemeral","ttl":"1h"}}]"""),
            Body(RewriteOperation.Default, "cache_control", """{"type":"ephemeral"}"""),
        ]);
        Assert.Equal(3, rewriter.CacheBreakpointsWritten);
    }
}
