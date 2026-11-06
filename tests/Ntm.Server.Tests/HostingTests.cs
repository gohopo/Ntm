using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ntm.Server.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task UnknownApiPathsAreJson404s()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("没有这个接口。", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnknownRelayPathsAreOpenAiShaped404s()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/v1/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("没有这个接口。", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnthropicClientsGetAnthropicShaped404s()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/batches");
        request.Headers.Add("x-api-key", "whatever");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", body.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AWrongMethodOnARelayPathIsStillJsonNotThePage()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/v1/chat/completions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PagePathsFallBackToTheAppOrSayItIsNotBuilt()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        bool built = host.Services.GetRequiredService<IWebHostEnvironment>()
            .WebRootFileProvider.GetFileInfo("200.html").Exists;

        using HttpResponseMessage response = await client.GetAsync("/channels/12");

        if (built)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoCache);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("管理页面没有构建", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MissingAssetsAre404sNotThePage()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/_app/immutable/missing.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AdminResponsesCarrySecurityHeadersButRelayResponsesDoNot()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage admin = await client.GetAsync("/api/auth/me");
        Assert.Equal("nosniff", Assert.Single(admin.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(admin.Headers.GetValues("X-Frame-Options")));

        using HttpResponseMessage relay = await client.GetAsync("/v1/models");
        Assert.False(relay.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task HealthCheckAnswers()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
