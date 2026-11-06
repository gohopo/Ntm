using System.Text;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

public class OpenAiChatRequestReaderTests
{
    private static OpenAiChatRequestReading Read(string json) =>
        OpenAiChatRequestReader.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ReadsModelAndStream()
    {
        OpenAiChatRequestReading reading = Read(
            """{"model":"gpt-4o","stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        Assert.True(reading.IsValid);
        Assert.Equal("gpt-4o", reading.Model);
        Assert.True(reading.IsStream);
        Assert.False(reading.HasStreamOptions);
        Assert.False(reading.ClientWantsUsage);
    }

    [Fact]
    public void NonStreamByDefault()
    {
        OpenAiChatRequestReading reading = Read("""{"model":"gpt-4o","messages":[]}""");

        Assert.True(reading.IsValid);
        Assert.False(reading.IsStream);
    }

    [Fact]
    public void StreamFalseIsNotStream()
    {
        Assert.False(Read("""{"model":"m","stream":false}""").IsStream);
    }

    [Fact]
    public void ReadsClientRequestedUsage()
    {
        OpenAiChatRequestReading reading = Read(
            """{"model":"m","stream":true,"stream_options":{"include_usage":true}}""");

        Assert.True(reading.HasStreamOptions);
        Assert.True(reading.ClientWantsUsage);
    }

    [Fact]
    public void StreamOptionsWithoutIncludeUsage()
    {
        OpenAiChatRequestReading reading = Read(
            """{"model":"m","stream":true,"stream_options":{"something":1}}""");

        Assert.True(reading.HasStreamOptions);
        Assert.False(reading.ClientWantsUsage);
    }

    [Fact]
    public void IncludeUsageFalseIsNotWanted()
    {
        OpenAiChatRequestReading reading = Read(
            """{"model":"m","stream":true,"stream_options":{"include_usage":false}}""");

        Assert.True(reading.HasStreamOptions);
        Assert.False(reading.ClientWantsUsage);
    }

    [Fact]
    public void NestedModelKeyIsIgnored()
    {
        // 工具定义里也可能出现 "model" 这个键，不能当成选路字段。
        OpenAiChatRequestReading reading = Read(
            """{"messages":[{"role":"user","content":"x"}],"metadata":{"model":"wrong"},"model":"right"}""");

        Assert.Equal("right", reading.Model);
    }

    [Fact]
    public void NestedStreamKeyIsIgnored()
    {
        OpenAiChatRequestReading reading = Read(
            """{"model":"m","tools":[{"function":{"parameters":{"stream":true}}}]}""");

        Assert.False(reading.IsStream);
    }

    [Fact]
    public void MissingModelStillValid()
    {
        OpenAiChatRequestReading reading = Read("""{"stream":true}""");

        Assert.True(reading.IsValid);
        Assert.Null(reading.Model);
    }

    [Fact]
    public void MalformedBodyIsInvalid()
    {
        Assert.False(Read("""{"model":"m",}}}""").IsValid);
        Assert.False(Read("not json").IsValid);
        Assert.False(Read("[]").IsValid);
        Assert.False(OpenAiChatRequestReader.Read([]).IsValid);
    }

    [Fact]
    public void OversizedBodyIsInvalid()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"model":"m"}""");

        Assert.False(OpenAiChatRequestReader.Read(body, maxBodyBytes: 4).IsValid);
    }
}
