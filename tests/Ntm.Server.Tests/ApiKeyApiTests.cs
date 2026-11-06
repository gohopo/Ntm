using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ntm.Server.Tests;

public sealed class ApiKeyApiTests
{
    [Fact]
    public async Task ApiKeysNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/api-keys");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheSecretIsShownOnceAndOnlyItsPrefixIsListed()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/api-keys", new { name = "  ci  " });

        Assert.Equal(HttpStatusCode.Created, status);
        string secret = body.GetProperty("secret").GetString()!;
        JsonElement view = body.GetProperty("view");
        Assert.StartsWith("ntm-", secret, StringComparison.Ordinal);
        Assert.Equal("ci", view.GetProperty("name").GetString());
        Assert.Equal(secret[..12], view.GetProperty("prefix").GetString());
        Assert.True(view.GetProperty("enabled").GetBoolean());

        JsonElement list = await admin.GetJsonAsync("/api/api-keys");
        string raw = list.GetRawText();
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("name").GetString() == "ci");
    }

    [Fact]
    public async Task ADisabledApiKeyIsRejectedRightAwayAndWorksAgainOnceEnabled()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        (_, JsonElement created) = await admin.PostAsync("/api/api-keys", new { name = "laptop" });
        string secret = created.GetProperty("secret").GetString()!;
        int id = created.GetProperty("view").GetProperty("id").GetInt32();
        using HttpClient client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await ListModelsAsync(client, secret));

        Assert.Equal(HttpStatusCode.NoContent, await admin.PatchAsync(AdminClient.ApiKey(id), new { enabled = false }));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListModelsAsync(client, secret));

        Assert.Equal(HttpStatusCode.NoContent, await admin.PatchAsync(AdminClient.ApiKey(id), new { enabled = true }));
        Assert.Equal(HttpStatusCode.OK, await ListModelsAsync(client, secret));

        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync(AdminClient.ApiKey(id)));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListModelsAsync(client, secret));
        Assert.Equal(HttpStatusCode.NotFound, await admin.DeleteAsync(AdminClient.ApiKey(id)));
    }

    [Fact]
    public async Task NamesMustBePresentAndUnique()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        (_, JsonElement first) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        (_, JsonElement second) = await admin.PostAsync("/api/api-keys", new { name = "deploy" });
        int secondId = second.GetProperty("view").GetProperty("id").GetInt32();

        (HttpStatusCode blank, JsonElement blankBody) = await admin.PostAsync("/api/api-keys", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank);
        Assert.Equal("请填写 API 密钥名。", blankBody.GetProperty("errors").GetProperty("name")[0].GetString());

        (HttpStatusCode duplicate, JsonElement duplicateBody) = await admin.PostAsync("/api/api-keys", new { name = "ci" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate);
        Assert.Equal("已经有同名的 API 密钥了。", duplicateBody.GetProperty("errors").GetProperty("name")[0].GetString());

        Assert.Equal(HttpStatusCode.BadRequest, await admin.PatchAsync(AdminClient.ApiKey(secondId), new { name = "ci" }));
        Assert.Equal(HttpStatusCode.NoContent, await admin.PatchAsync(AdminClient.ApiKey(secondId), new { name = "deploy-2" }));
        Assert.Equal(HttpStatusCode.NotFound, await admin.PatchAsync(AdminClient.ApiKey(9999), new { enabled = false }));

        JsonElement list = await admin.GetJsonAsync("/api/api-keys");
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("name").GetString() == "deploy-2");
        Assert.Equal(first.GetProperty("view").GetProperty("id").GetInt32(),
            list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "ci").GetProperty("id").GetInt32());
    }

    private static async Task<HttpStatusCode> ListModelsAsync(HttpClient client, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
