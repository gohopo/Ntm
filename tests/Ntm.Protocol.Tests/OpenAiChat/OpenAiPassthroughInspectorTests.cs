using System.Text;
using Ntm.Protocol.OpenAiChat;

namespace Ntm.Protocol.Tests.OpenAiChat;

/// <summary>嵌入和图片生成的响应读取（Q228–Q232）与端点推导（Q224）。</summary>
public sealed class OpenAiPassthroughInspectorTests
{
    [Theory]
    [InlineData("/v1/chat/completions", RequestKind.Embedding, "/v1/embeddings")]
    [InlineData("/api/v3/chat/completions", RequestKind.Image, "/api/v3/images/generations")]
    [InlineData("/openai/chat/completions/?api-version=2024-10-21", RequestKind.Embedding, "/openai/embeddings?api-version=2024-10-21")]
    [InlineData("/v1/chat/completions", RequestKind.Chat, "/v1/chat/completions")]
    [InlineData("/v1/generate", RequestKind.Embedding, null)]
    [InlineData("/v1/responses", RequestKind.Image, null)]
    public void DerivesTheKindEndpointFromAChatPath(string chatPath, RequestKind kind, string? expected) =>
        Assert.Equal(expected, ApiFormatEndpoints.DeriveRequestPath(chatPath, kind));

    [Fact]
    public void ReadsEmbeddingUsageFromPromptTokensOrTotalTokens()
    {
        OpenAiPassthroughResponseReading withPrompt = Read(
            "{\"data\":[{\"embedding\":[0.1]}],\"model\":\"m\",\"usage\":{\"prompt_tokens\":7,\"total_tokens\":9}}",
            RequestKind.Embedding);
        OpenAiPassthroughResponseReading totalOnly = Read(
            "{\"data\":[{\"embedding\":\"AAAA\"}],\"usage\":{\"total_tokens\":5}}",
            RequestKind.Embedding);

        Assert.Equal(7, withPrompt.Usage.InputTokens);
        Assert.Equal("m", withPrompt.UpstreamModel);
        Assert.Equal(5, totalOnly.Usage.InputTokens);
        Assert.Equal(0, totalOnly.Usage.OutputTokens);
        Assert.False(totalOnly.IsEmpty);
    }

    [Theory]
    [InlineData("{\"data\":[{\"b64_json\":\"x\"},{\"b64_json\":\"y\"}]}", 2)]
    [InlineData("{\"images\":[{\"url\":\"https://a\"}],\"seed\":1}", 1)]
    [InlineData("{\"data\":[{\"url\":\"https://a\"}],\"usage\":{\"generated_images\":3,\"output_tokens\":1}}", 3)]
    public void CountsImagesAcrossProviderShapes(string json, int expected) =>
        Assert.Equal(expected, Read(json, RequestKind.Image).ItemCount);

    [Fact]
    public void ReadsImageUsageInEitherNaming()
    {
        Usage openAi = Read("{\"data\":[{}],\"usage\":{\"input_tokens\":10,\"output_tokens\":20}}", RequestKind.Image).Usage;
        Usage chatNaming = Read("{\"data\":[{}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}", RequestKind.Image).Usage;
        Usage none = Read("{\"data\":[{}]}", RequestKind.Image).Usage;

        Assert.Equal((10, 20), (openAi.InputTokens, openAi.OutputTokens));
        Assert.Equal((3, 4), (chatNaming.InputTokens, chatNaming.OutputTokens));
        Assert.False(none.HasValue);
    }

    [Theory]
    [InlineData("{\"data\":[]}", RequestKind.Image)]
    [InlineData("{\"created\":1}", RequestKind.Image)]
    [InlineData("{\"data\":[],\"images\":[]}", RequestKind.Image)]
    [InlineData("{\"object\":\"list\",\"data\":[]}", RequestKind.Embedding)]
    public void AnEmptyResultIsReportedAsEmpty(string json, RequestKind kind) =>
        Assert.True(Read(json, kind).IsEmpty);

    [Fact]
    public void ATopLevelErrorIsAnError()
    {
        OpenAiPassthroughResponseReading reading = Read("{\"error\":{\"message\":\"nope\"}}", RequestKind.Embedding);
        OpenAiPassthroughResponseReading malformed = Read("[1,2]", RequestKind.Embedding);

        Assert.True(reading.IsError);
        Assert.True(malformed.IsMalformed);
    }

    [Fact]
    public void TheImageStreamCommitsOnThePartialImageAndCountsCompletedOnes()
    {
        var inspector = new OpenAiImageStreamInspector();

        Assert.Equal(StreamFrameVerdict.Content, Inspect(inspector, "{\"type\":\"image_generation.partial_image\",\"b64_json\":\"p\"}"));
        Assert.Equal(StreamFrameVerdict.Content, Inspect(
            inspector,
            "{\"type\":\"image_generation.completed\",\"b64_json\":\"f\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}"));

        Assert.True(inspector.SawTerminal);
        Assert.Equal(1, inspector.ImageCount);
        Assert.Equal(2, inspector.Usage.OutputTokens);
        Assert.False(inspector.EndedEmpty);
    }

    [Fact]
    public void AVolcengineStreamWithOnlyFailuresEndsEmptyWithTheFailure()
    {
        var inspector = new OpenAiImageStreamInspector();

        Assert.Equal(StreamFrameVerdict.Preamble, Inspect(
            inspector,
            "{\"type\":\"image_generation.partial_failed\",\"error\":{\"message\":\"blocked\"}}"));
        Assert.Equal(StreamFrameVerdict.UsageOnly, Inspect(
            inspector,
            "{\"type\":\"image_generation.completed\",\"usage\":{\"generated_images\":0,\"output_tokens\":0}}"));
        Assert.Equal(StreamFrameVerdict.Terminal, Inspect(inspector, "[DONE]"));

        Assert.Equal(0, inspector.ImageCount);
        Assert.True(inspector.EndedEmpty);
        Assert.Contains("blocked", inspector.PartialFailure);
        Assert.Null(inspector.ErrorPayload);
    }

    [Fact]
    public void AStreamErrorEventIsAnError()
    {
        var inspector = new OpenAiImageStreamInspector();

        Assert.Equal(StreamFrameVerdict.Error, Inspect(inspector, "{\"type\":\"error\",\"error\":{\"message\":\"x\"}}"));
        Assert.NotNull(inspector.ErrorPayload);
    }

    private static OpenAiPassthroughResponseReading Read(string json, RequestKind kind) =>
        OpenAiPassthroughResponseInspector.Read(Encoding.UTF8.GetBytes(json), kind);

    private static StreamFrameVerdict Inspect(OpenAiImageStreamInspector inspector, string data) =>
        inspector.Inspect(Encoding.UTF8.GetBytes(data));
}
