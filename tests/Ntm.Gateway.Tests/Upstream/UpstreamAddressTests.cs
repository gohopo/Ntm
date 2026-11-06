using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Upstream;

public sealed class UpstreamAddressTests
{
    [Theory]
    // 粘 SDK 风格的 base_url：版本段被剥成路径前缀，不会拼出 /v1/v1/...
    [InlineData("https://api.openai.com/v1", "https://api.openai.com", "/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com", "/v1")]
    [InlineData("https://api.openai.com", "https://api.openai.com", "")]
    // 火山引擎：路径前缀 + 非 v1 版本段
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api", "/v3")]
    // Gemini 的 v1beta
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "https://generativelanguage.googleapis.com", "/v1beta")]
    // 自建反代挂在子路径下
    [InlineData("https://gw.example.com/llm/openai/v1", "https://gw.example.com/llm/openai", "/v1")]
    // 端口与大写协议
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434", "/v1")]
    public void NormalizesBaseUrl(string input, string expectedBase, string expectedVersion)
    {
        Assert.True(UpstreamAddress.TryNormalizeBaseUrl(input, out string? baseUrl, out string version));

        Assert.Equal(expectedBase, baseUrl);
        Assert.Equal(expectedVersion, version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("ws://example.com")]
    public void RejectsNonHttpInput(string? input)
    {
        Assert.False(UpstreamAddress.TryNormalizeBaseUrl(input, out _, out _));
    }

    [Fact]
    public void PlainNumericSegmentIsNotAVersion()
    {
        Assert.True(UpstreamAddress.TryNormalizeBaseUrl("https://example.com/api/3", out string? baseUrl, out string version));

        Assert.Equal("https://example.com/api/3", baseUrl);
        Assert.Equal("", version);
    }

    [Fact]
    public void StripsSeveralTrailingVersionSegments()
    {
        // 手滑粘了两段版本。
        Assert.True(UpstreamAddress.TryNormalizeBaseUrl("https://example.com/api/v3/v1", out string? baseUrl, out string version));

        Assert.Equal("https://example.com/api", baseUrl);
        Assert.Equal("/v3/v1", version);
    }

    [Theory]
    [InlineData(ApiFormat.OpenAiChat, RequestKind.Chat, "https://api.openai.com/v1/chat/completions")]
    [InlineData(ApiFormat.OpenAiChat, RequestKind.Embedding, "https://api.openai.com/v1/embeddings")]
    [InlineData(ApiFormat.OpenAiResponses, RequestKind.Chat, "https://api.openai.com/v1/responses")]
    [InlineData(ApiFormat.Anthropic, RequestKind.Chat, "https://api.openai.com/v1/messages")]
    public void EmptyPathFallsBackToFormatDefault(ApiFormat format, RequestKind kind, string expected)
    {
        Assert.True(UpstreamAddress.TryBuildRequestUri("https://api.openai.com", null, format, kind, out Uri? uri));

        Assert.Equal(expected, uri.ToString());
    }

    [Fact]
    public void ExplicitPathWins()
    {
        Assert.True(UpstreamAddress.TryBuildRequestUri(
            "https://ark.cn-beijing.volces.com",
            "/api/v3/chat/completions",
            ApiFormat.OpenAiChat,
            RequestKind.Chat,
            out Uri? uri));

        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/chat/completions", uri.ToString());
    }

    [Fact]
    public void PathWithoutLeadingSlashIsAccepted()
    {
        Assert.True(UpstreamAddress.TryBuildRequestUri(
            "https://example.com", "custom/chat", ApiFormat.OpenAiChat, RequestKind.Chat, out Uri? uri));

        Assert.Equal("https://example.com/custom/chat", uri.ToString());
    }

    [Fact]
    public void TrailingSlashOnBaseUrlDoesNotDoubleUp()
    {
        Assert.True(UpstreamAddress.TryBuildRequestUri(
            "https://example.com/", "/v1/chat/completions", ApiFormat.OpenAiChat, RequestKind.Chat, out Uri? uri));

        Assert.Equal("https://example.com/v1/chat/completions", uri.ToString());
    }

    [Fact]
    public void UnsupportedKindForFormatFails()
    {
        // Anthropic 没有嵌入端点。选路应该先用 ApiFormatCapability 把它过滤掉。
        Assert.False(UpstreamAddress.TryBuildRequestUri(
            "https://api.anthropic.com", null, ApiFormat.Anthropic, RequestKind.Embedding, out _));
    }

    [Theory]
    [InlineData("/v1/chat/completions", "/v1/models")]
    [InlineData("/api/v3/chat/completions", "/api/v3/models")]
    [InlineData("/v1/messages", "/v1/models")]
    [InlineData("/v1/responses", "/v1/models")]
    [InlineData("/v1/embeddings", "/v1/models")]
    public void DerivesModelsPathBySwappingKnownSuffix(string requestPath, string expected)
    {
        Assert.Equal(expected, ApiFormatEndpoints.DeriveModelsPath(requestPath));
    }

    [Fact]
    public void UnknownSuffixYieldsNoModelsPath()
    {
        // 推不出来就让界面问，而不是报错。
        Assert.Null(ApiFormatEndpoints.DeriveModelsPath("/weird/endpoint"));
    }
}
