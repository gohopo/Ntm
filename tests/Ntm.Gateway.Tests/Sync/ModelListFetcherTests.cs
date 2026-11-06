using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Sync;

public sealed class ModelListFetcherTests
{
    [Fact]
    public async Task OpenAiListIsReadFromDataIdsWithBearerAuth()
    {
        using var upstream = new Recorder(_ => Json("""{"object":"list","data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"gpt-4o"}]}"""));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example/", null, ApiFormat.OpenAiChat, null));

        Assert.True(result.Succeeded);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], result.Models);
        (Uri uri, Dictionary<string, string> headers) = Assert.Single(upstream.Requests);
        Assert.Equal("https://up.example/v1/models", uri.ToString());
        Assert.Equal("Bearer sk-test", headers["Authorization"]);
    }

    [Theory]
    [InlineData("/api/v3/chat/completions", "https://up.example/api/v3/models")]
    [InlineData("/openai/v1/responses", "https://up.example/openai/v1/models")]
    [InlineData(null, "https://up.example/v1/models")]
    public void TheModelsAddressIsDerivedFromTheChannelPath(string? path, string expected)
    {
        Uri? uri = ModelListFetcher.ModelsUri(new ModelListTarget("https://up.example", path, ApiFormat.OpenAiChat, null));
        Assert.Equal(expected, uri?.ToString());
    }

    [Fact]
    public async Task APathThatDoesNotEndInAKnownSuffixFails()
    {
        using var upstream = new Recorder(_ => Json("{}"));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example", "/custom/endpoint", ApiFormat.OpenAiChat, null));

        Assert.False(result.Succeeded);
        Assert.Contains("推不出列模型的地址", result.Error, StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task AnthropicListsArePagedWithXApiKey()
    {
        using var upstream = new Recorder(uri => uri.Query.Contains("after_id=b", StringComparison.Ordinal)
            ? Json("""{"data":[{"id":"c"}],"has_more":false,"last_id":"c"}""")
            : Json("""{"data":[{"id":"a"},{"id":"b"}],"has_more":true,"first_id":"a","last_id":"b"}"""));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://api.anthropic.example", null, ApiFormat.Anthropic, null));

        Assert.True(result.Succeeded);
        Assert.Equal(["a", "b", "c"], result.Models);
        Assert.Equal(2, upstream.Requests.Count);
        (Uri first, Dictionary<string, string> headers) = upstream.Requests.First();
        Assert.Contains("limit=1000", first.Query, StringComparison.Ordinal);
        Assert.Equal("sk-test", headers["x-api-key"]);
        Assert.True(headers.ContainsKey("anthropic-version"));
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AnEmptyListIsAFailureSoTheExistingListIsKept()
    {
        using var upstream = new Recorder(_ => Json("""{"data":[]}"""));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example", null, ApiFormat.OpenAiChat, null));

        Assert.False(result.Succeeded);
        Assert.Contains("空的模型清单", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"models":["a"]}""")]
    [InlineData("""[{"id":"a"}]""")]
    [InlineData("""{"data":[{"name":"a"}]}""")]
    [InlineData("not json")]
    public async Task UnrecognisedShapesFail(string body)
    {
        using var upstream = new Recorder(_ => Json(body));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example", null, ApiFormat.OpenAiChat, null));

        Assert.False(result.Succeeded);
        Assert.Contains("无法识别的模型列表", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorStatusesCarryTheStatusAndASnippet()
    {
        using var upstream = new Recorder(_ => Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example", null, ApiFormat.OpenAiChat, null));

        Assert.False(result.Succeeded);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.Contains("HTTP 401", result.Error, StringComparison.Ordinal);
        Assert.Contains("bad key", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanTheLimitFails()
    {
        string ids = string.Join(",", Enumerable.Range(0, ModelListFetcher.MaxModels + 1).Select(i => $"{{\"id\":\"m{i}\"}}"));
        using var upstream = new Recorder(_ => Json($$"""{"data":[{{ids}}]}"""));
        ModelListResult result = await Fetch(upstream, new ModelListTarget("https://up.example", null, ApiFormat.OpenAiChat, null));

        Assert.False(result.Succeeded);
        Assert.Contains("超过 2000 个模型", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorsThroughAProxyNameTheProxy()
    {
        Assert.True(ProxyAddress.TryParse("http://127.0.0.1:1", out ProxyAddress? proxy, out _));
        using var factory = new UpstreamHttpClientFactory();
        var fetcher = new ModelListFetcher(factory);

        ModelListResult result = await fetcher.FetchAsync(
            new ModelListTarget("http://up.example", null, ApiFormat.OpenAiChat, proxy),
            "sk-test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("经代理 127.0.0.1:1：", result.Error, StringComparison.Ordinal);
    }

    private static Task<ModelListResult> Fetch(Recorder upstream, ModelListTarget target) =>
        new ModelListFetcher(new HttpMessageInvoker(upstream, disposeHandler: false)).FetchAsync(target, "sk-test", CancellationToken.None);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Recorder(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Dictionary<string, string> headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Enqueue((request.RequestUri!, headers));
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
