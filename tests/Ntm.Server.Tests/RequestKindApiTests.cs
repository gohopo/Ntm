using System.Net;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>嵌入和图片生成的管理面（Q222、Q224、Q230、Q236–Q238）。</summary>
public sealed class RequestKindApiTests
{
    private static readonly string[] Models = ["text-embedding-3-small"];

    [Fact]
    public async Task AModelCanBeCreatedAsAnEmbeddingModelAndChangedLater()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/models", new
        {
            name = "embed",
            requestKind = "embedding",
            rules = new object[] { new { kind = "exact", pattern = "text-embedding-3-small" } },
        });

        Assert.Equal(HttpStatusCode.Created, status);
        int id = body.GetProperty("model").GetProperty("id").GetInt32();
        Assert.Equal("embedding", body.GetProperty("model").GetProperty("requestKind").GetString());

        (HttpStatusCode updated, JsonElement changed) = await admin.PutAsync(AdminClient.Model(id), new
        {
            name = "embed",
            requestKind = "image",
            rules = new object[] { new { kind = "exact", pattern = "text-embedding-3-small" } },
        });

        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal("image", changed.GetProperty("model").GetProperty("requestKind").GetString());

        (HttpStatusCode rejected, JsonElement problem) = await admin.PostAsync("/api/models", new
        {
            name = "speak",
            requestKind = "audio",
            rules = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, rejected);
        Assert.Contains("requestKind", problem.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task PreviewExplainsWhyAChannelCannotServeEmbeddings()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int openAi = await admin.CreateChannelAsync("alpha", Models);
        await admin.AddKeyAsync(openAi, "sk-alpha-0123456789");

        (HttpStatusCode created, JsonElement custom) = await admin.PostAsync("/api/channels", new
        {
            name = "bravo",
            baseUrl = "https://bravo.example",
            path = "/generate",
            provider = "openai",
            upstreamModels = Models,
        });
        Assert.Equal(HttpStatusCode.Created, created);
        await admin.AddKeyAsync(custom.GetProperty("channel").GetProperty("id").GetInt32(), "sk-bravo-0123456789");

        (HttpStatusCode claudeCreated, JsonElement claude) = await admin.PostAsync("/api/channels", new
        {
            name = "charlie",
            baseUrl = "https://charlie.example",
            apiFormat = "anthropic",
            provider = "anthropic",
            upstreamModels = Models,
        });
        Assert.Equal(HttpStatusCode.Created, claudeCreated);
        await admin.AddKeyAsync(claude.GetProperty("channel").GetProperty("id").GetInt32(), "sk-charlie-0123456789");

        (_, JsonElement preview) = await admin.PostAsync("/api/models/preview", new
        {
            requestKind = "embedding",
            rules = new object[] { new { kind = "exact", pattern = "text-embedding-3-small" } },
        });

        Assert.Equal(1, preview.GetProperty("candidateCount").GetInt32());
        Dictionary<string, string?> reasons = preview.GetProperty("excluded").EnumerateArray()
            .ToDictionary(e => e.GetProperty("channelName").GetString()!, e => e.GetProperty("reason").GetString());
        Assert.Contains("自定义路径", reasons["bravo"]);
        Assert.Equal("嵌入和图片只能用 OpenAI Chat 格式的渠道。", reasons["charlie"]);
    }

    [Fact]
    public async Task APriceRowTakesAPerImagePrice()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateChannelAsync("alpha", ["no-such-image-model"]);
        JsonElement page = await admin.GetJsonAsync("/api/prices");
        JsonElement first = page.GetProperty("items")[0];
        int id = first.GetProperty("id").GetInt32();
        Assert.True(first.GetProperty("unpriced").GetBoolean());

        (HttpStatusCode status, JsonElement row) = await admin.PutAsync("/api/prices/" + id, new { imagePrice = 0.04m });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0.04m, row.GetProperty("imagePrice").GetDecimal());
        Assert.False(row.GetProperty("unpriced").GetBoolean());
        Assert.Equal("manual", row.GetProperty("source").GetString());

        (HttpStatusCode bad, JsonElement problem) = await admin.PutAsync("/api/prices/" + id, new { imagePrice = -1m });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("imagePrice", problem.GetProperty("errors").EnumerateObject().Select(p => p.Name));

        (_, JsonElement restored) = await admin.PostAsync("/api/prices/" + id + "/auto", new { });
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("imagePrice").ValueKind);
    }

    [Fact]
    public async Task TheRequestListFiltersByKind()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode ok, _) = await admin.GetAsync("/api/requests?kind=image");
        (HttpStatusCode bad, JsonElement problem) = await admin.GetAsync("/api/requests?kind=nope");

        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("kind", problem.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheWorkbenchRejectsAnUnknownKindAndNonOpenAiChannelsForEmbeddings()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        (_, JsonElement claude) = await admin.PostAsync("/api/channels", new
        {
            name = "charlie",
            baseUrl = "https://charlie.example",
            apiFormat = "anthropic",
            provider = "anthropic",
        });
        int id = claude.GetProperty("channel").GetProperty("id").GetInt32();

        (HttpStatusCode unknown, _) = await admin.PostAsync($"/api/workbench/channels/{id}/send?kind=audio", new { model = "x" });
        (HttpStatusCode wrongFormat, JsonElement problem) = await admin.PostAsync(
            $"/api/workbench/channels/{id}/send?kind=embedding",
            new { model = "x", input = "hi" });

        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.Equal(HttpStatusCode.BadRequest, wrongFormat);
        Assert.Contains("OpenAI Chat", problem.GetProperty("errors").GetProperty("kind")[0].GetString());
    }
}
