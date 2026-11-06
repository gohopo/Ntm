using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ntm.Server.Tests;

public sealed class ModelApiTests
{
    private static readonly string[] OpenAiModels = ["gpt-4.1", "gpt-4.1-mini", "gpt-4x1"];

    private static readonly string[] MiniExcluded = ["gpt-4\\.1-mini"];

    [Fact]
    public async Task ModelsNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ANewModelIsNotPublishedUntilEnabledButItsRouteIsAlreadyShown()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("alpha", OpenAiModels);
        await admin.AddKeyAsync(channel, "sk-alpha-0123456789");
        string token = await CreateApiKeyAsync(admin);
        using HttpClient client = host.CreateClient();

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/models", new
        {
            name = "gpt-4.1",
            rules = new object[] { new { kind = "exact", pattern = "gpt-4.1" } },
        });

        Assert.Equal(HttpStatusCode.Created, status);
        int id = body.GetProperty("model").GetProperty("id").GetInt32();
        Assert.False(body.GetProperty("model").GetProperty("enabled").GetBoolean());
        JsonElement route = body.GetProperty("route");
        Assert.Equal(1, route.GetProperty("candidateCount").GetInt32());
        JsonElement candidate = route.GetProperty("priorityGroups")[0].GetProperty("candidates")[0];
        Assert.Equal("gpt-4.1", candidate.GetProperty("upstreamModel").GetString());
        Assert.Equal(1, candidate.GetProperty("ruleOrdinal").GetInt32());
        Assert.DoesNotContain("gpt-4.1", await PublishedNamesAsync(client, token));

        Assert.Equal(HttpStatusCode.NoContent, await admin.PatchAsync(AdminClient.Model(id), new { enabled = true }));
        Assert.Contains("gpt-4.1", await PublishedNamesAsync(client, token));

        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync(AdminClient.Model(id)));
        Assert.DoesNotContain("gpt-4.1", await PublishedNamesAsync(client, token));
        Assert.Equal(HttpStatusCode.NotFound, await admin.DeleteAsync(AdminClient.Model(id)));
    }

    [Fact]
    public async Task PreviewUsesTheRealMatcherAndShowsTheAnchoredPattern()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("alpha", OpenAiModels);
        await admin.AddKeyAsync(channel, "sk-alpha-0123456789");

        (HttpStatusCode status, JsonElement preview) = await admin.PostAsync("/api/models/preview", new
        {
            rules = new object[]
            {
                new { kind = "regex", pattern = "^gpt-4\\.1.*$", excludedPatterns = MiniExcluded },
                new { kind = "exact", pattern = "gpt-4.1" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        List<string?> upstream = [.. preview.GetProperty("priorityGroups").EnumerateArray()
            .SelectMany(t => t.GetProperty("candidates").EnumerateArray())
            .Select(c => c.GetProperty("upstreamModel").GetString())];
        Assert.Equal(["gpt-4.1"], upstream);

        JsonElement first = preview.GetProperty("rules")[0];
        Assert.Equal("^(?:gpt-4\\.1.*)$", first.GetProperty("effectivePattern").GetString());
        Assert.Equal(1, first.GetProperty("matchCount").GetInt32());
        // 同一个落点先到先得：第二条规则什么都没拿到。
        Assert.Equal(0, preview.GetProperty("rules")[1].GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task PreviewExplainsWhyAMatchingChannelIsNotACandidate()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int withKey = await admin.CreateChannelAsync("alpha", OpenAiModels);
        await admin.AddKeyAsync(withKey, "sk-alpha-0123456789");
        await admin.CreateChannelAsync("bravo", OpenAiModels);
        int disabled = await admin.CreateChannelAsync("charlie", OpenAiModels, enabled: false);
        await admin.AddKeyAsync(disabled, "sk-charlie-0123456789");

        (_, JsonElement preview) = await admin.PostAsync("/api/models/preview", new
        {
            rules = new object[] { new { kind = "exact", pattern = "gpt-4.1" } },
        });

        Assert.Equal(1, preview.GetProperty("candidateCount").GetInt32());
        Dictionary<string, string?> reasons = preview.GetProperty("excluded").EnumerateArray()
            .ToDictionary(e => e.GetProperty("channelName").GetString()!, e => e.GetProperty("reason").GetString());
        Assert.Equal("渠道没有启用的密钥。", reasons["bravo"]);
        Assert.Equal("渠道已停用。", reasons["charlie"]);
    }

    [Fact]
    public async Task PreviewReportsABrokenDraftRuleInsteadOfFailing()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement preview) = await admin.PostAsync("/api/models/preview", new
        {
            rules = new object[] { new { kind = "regex", pattern = "gpt-(?=4)" } },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, preview.GetProperty("candidateCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(preview.GetProperty("rules")[0].GetProperty("problem").GetString()));
    }

    [Fact]
    public async Task SavingRejectsBadNamesPatternsAndChannels()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateModelAsync("taken", []);

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/models", new
        {
            name = "has space",
            rules = new object[]
            {
                new { kind = "regex", pattern = "gpt-(?=4)" },
                new { kind = "exact", pattern = "gpt-4.1", channelId = 9999 },
                new { kind = "exact", pattern = "gpt-4.1", weight = 0 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        JsonElement errors = body.GetProperty("errors");
        Assert.Equal("模型名里不能有空格。", errors.GetProperty("name")[0].GetString());
        Assert.StartsWith("正则用了不支持的写法", errors.GetProperty("rules[0].pattern")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("指定的渠道不存在。", errors.GetProperty("rules[1].channelId")[0].GetString());
        Assert.Equal("权重要在 1 到 1000 之间。", errors.GetProperty("rules[2].weight")[0].GetString());

        (HttpStatusCode duplicate, JsonElement duplicateBody) = await admin.PostAsync("/api/models", new { name = "taken" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate);
        Assert.Equal("已经有同名的下发模型了。", duplicateBody.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task SavingReplacesTheRulesInOrder()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateModelAsync("gpt-4.1", [new { kind = "exact", pattern = "gpt-4.1" }]);

        (HttpStatusCode status, JsonElement body) = await admin.PutAsync(AdminClient.Model(id), new
        {
            name = "gpt-4.1",
            displayName = "GPT-4.1",
            enabled = false,
            rules = new object[]
            {
                new { kind = "regex", pattern = "gpt-4\\.1-.*" },
                new { kind = "exact", pattern = "gpt-4.1", priority = 5 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement rules = body.GetProperty("rules");
        Assert.Equal(2, rules.GetArrayLength());
        Assert.Equal("regex", rules[0].GetProperty("kind").GetString());
        Assert.Equal(1, rules[0].GetProperty("ordinal").GetInt32());
        Assert.Equal(5, rules[1].GetProperty("priority").GetInt32());
        Assert.Equal("GPT-4.1", body.GetProperty("model").GetProperty("displayName").GetString());

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Model(id));
        Assert.Equal(2, detail.GetProperty("rules").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(AdminClient.Model(9999))).Status);
    }

    private static async Task<string> CreateApiKeyAsync(AdminClient admin)
    {
        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/api-keys", new { name = "test" });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("secret").GetString()!;
    }

    private static async Task<List<string?>> PublishedNamesAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString())];
    }
}
