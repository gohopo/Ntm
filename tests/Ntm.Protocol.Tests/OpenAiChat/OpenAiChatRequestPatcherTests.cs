using System.Text;
using System.Text.Json;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

public class OpenAiChatRequestPatcherTests
{
    private static bool TryPatch(string json, out string result)
    {
        bool patched = OpenAiChatRequestPatcher.TryEnsureIncludeUsage(
            Encoding.UTF8.GetBytes(json),
            out byte[] bytes);
        result = patched ? Encoding.UTF8.GetString(bytes) : json;
        return patched;
    }

    [Fact]
    public void AddsStreamOptionsWhenAbsent()
    {
        Assert.True(TryPatch("""{"model":"gpt-4o","stream":true}""", out string result));

        Assert.Equal(
            """{"model":"gpt-4o","stream":true,"stream_options":{"include_usage":true}}""",
            result);
    }

    [Fact]
    public void ReplacesExistingStreamOptions()
    {
        Assert.True(TryPatch(
            """{"model":"m","stream_options":{"include_usage":false},"stream":true}""",
            out string result));

        Assert.Equal(
            """{"model":"m","stream_options":{"include_usage":true},"stream":true}""",
            result);
    }

    [Fact]
    public void ReplacesNullStreamOptions()
    {
        Assert.True(TryPatch("""{"model":"m","stream_options":null}""", out string result));

        Assert.Equal("""{"model":"m","stream_options":{"include_usage":true}}""", result);
    }

    [Fact]
    public void AddsToEmptyObject()
    {
        Assert.True(TryPatch("{}", out string result));

        Assert.Equal("""{"stream_options":{"include_usage":true}}""", result);
    }

    [Fact]
    public void LeavesEveryOtherByteAlone()
    {
        // 这个格式特有的字段必须一个不少地透过去。
        const string Body = """
            {"model":"gpt-4o","stream":true,"messages":[{"role":"user","content":"你好"}],"logit_bias":{"1234":-100},"metadata":{"a":"b"}}
            """;

        Assert.True(TryPatch(Body, out string result));

        using JsonDocument before = JsonDocument.Parse(Body);
        using JsonDocument after = JsonDocument.Parse(result);
        foreach (JsonProperty property in before.RootElement.EnumerateObject())
        {
            Assert.Equal(
                property.Value.GetRawText(),
                after.RootElement.GetProperty(property.Name).GetRawText());
        }

        Assert.True(after.RootElement
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean());
    }

    [Fact]
    public void KeepsWhitespaceFormatting()
    {
        const string Body = "{\n  \"model\": \"m\",\n  \"stream\": true\n}";

        Assert.True(TryPatch(Body, out string result));

        Assert.StartsWith("{\n  \"model\": \"m\",\n  \"stream\": true\n", result, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(result);
        Assert.True(document.RootElement
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean());
    }

    [Fact]
    public void NestedStreamOptionsIsNotTouched()
    {
        Assert.True(TryPatch(
            """{"model":"m","metadata":{"stream_options":{"include_usage":false}}}""",
            out string result));

        using JsonDocument document = JsonDocument.Parse(result);
        Assert.False(document.RootElement
            .GetProperty("metadata")
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean());
        Assert.True(document.RootElement
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean());
    }

    [Fact]
    public void ProducesParseableJsonForEveryShape()
    {
        string[] bodies =
        [
            """{"model":"m"}""",
            """{"stream":true,"model":"m","stream_options":{"include_usage":true}}""",
            """{"a":[1,2,{"b":"}"}],"model":"m"}""",
            """{"model":"m","text":"} not a brace"}""",
        ];

        foreach (string body in bodies)
        {
            Assert.True(TryPatch(body, out string result), body);
            using JsonDocument document = JsonDocument.Parse(result);
            Assert.True(document.RootElement
                .GetProperty("stream_options")
                .GetProperty("include_usage")
                .GetBoolean());
        }
    }

    [Fact]
    public void RefusesMalformedBody()
    {
        Assert.False(TryPatch("not json", out _));
        Assert.False(TryPatch("[]", out _));
        Assert.False(OpenAiChatRequestPatcher.TryEnsureIncludeUsage([], out _));
    }

    private static bool TryRename(string json, string model, out string result)
    {
        bool patched = OpenAiChatRequestPatcher.TryReplaceModel(
            Encoding.UTF8.GetBytes(json),
            model,
            out byte[] bytes);
        result = patched ? Encoding.UTF8.GetString(bytes) : json;
        return patched;
    }

    [Fact]
    public void ReplacesTheModelAndNothingElse()
    {
        // 客户端说的是 PublishedModel，上游认的是 UpstreamModel：只换这一个值。
        Assert.True(TryRename(
            """{"model":"fast","messages":[{"role":"user","content":"model"}],"stream":true}""",
            "gpt-4o-2024-11-20",
            out string result));

        Assert.Equal(
            """{"model":"gpt-4o-2024-11-20","messages":[{"role":"user","content":"model"}],"stream":true}""",
            result);
    }

    [Fact]
    public void ReplacingTheModelKeepsWhitespaceAndOrder()
    {
        Assert.True(TryRename(
            "{\n  \"messages\": [],\n  \"model\": \"fast\"\n}",
            "gpt-4o",
            out string result));

        Assert.Equal("{\n  \"messages\": [],\n  \"model\": \"gpt-4o\"\n}", result);
    }

    [Fact]
    public void TheSameNameIsNotRewritten()
    {
        // 名字一样就没必要复制一份字节。
        Assert.False(TryRename("""{"model":"gpt-4o"}""", "gpt-4o", out string result));
        Assert.Equal("""{"model":"gpt-4o"}""", result);
    }

    [Fact]
    public void ANestedModelKeyIsNotTouched()
    {
        Assert.True(TryRename(
            """{"metadata":{"model":"inner"},"model":"outer"}""",
            "real",
            out string result));

        Assert.Equal("""{"metadata":{"model":"inner"},"model":"real"}""", result);
    }

    [Fact]
    public void ANameNeedingEscapingIsEncodedAsJson()
    {
        Assert.True(TryRename("""{"model":"a"}""", "wei\"rd", out string result));

        Assert.Equal("""{"model":"wei\u0022rd"}""", result);
        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("wei\"rd", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void AMissingOrNonStringModelIsRefused()
    {
        Assert.False(TryRename("""{"messages":[]}""", "gpt-4o", out _));
        Assert.False(TryRename("""{"model":123}""", "gpt-4o", out _));
        Assert.False(TryRename("not json", "gpt-4o", out _));
        Assert.False(TryRename("", "gpt-4o", out _));
    }
}
