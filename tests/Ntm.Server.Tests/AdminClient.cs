using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>登录过的管理端客户端，外加几个建数据的捷径。</summary>
internal sealed class AdminClient : IDisposable
{
    private AdminClient(HttpClient client) => Client = client;

    public HttpClient Client { get; }

    public static async Task<AdminClient> SignInAsync(ServerHost host)
    {
        HttpClient client = host.CreateBrowser();
        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/auth/login", new { password = ServerHost.Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return new AdminClient(client);
    }

    public static string Channel(int id) => "/api/channels/" + id.ToString(CultureInfo.InvariantCulture);

    public static string Model(int id) => "/api/models/" + id.ToString(CultureInfo.InvariantCulture);

    public static string ApiKey(int id) => "/api/api-keys/" + id.ToString(CultureInfo.InvariantCulture);

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        using HttpResponseMessage response = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url)
    {
        using HttpResponseMessage response = await Client.GetAsync(url);
        return (response.StatusCode, await ReadBodyAsync(response));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object input)
    {
        using HttpResponseMessage response = await Client.PostAsJsonAsync(url, input);
        return (response.StatusCode, await ReadBodyAsync(response));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string url, object input)
    {
        using HttpResponseMessage response = await Client.PutAsJsonAsync(url, input);
        return (response.StatusCode, await ReadBodyAsync(response));
    }

    public async Task<HttpStatusCode> PatchAsync(string url, object input)
    {
        using HttpResponseMessage response = await Client.PatchAsJsonAsync(url, input);
        return response.StatusCode;
    }

    public async Task<HttpStatusCode> DeleteAsync(string url)
    {
        using HttpResponseMessage response = await Client.DeleteAsync(url);
        return response.StatusCode;
    }

    /// <summary>建一个渠道，返回它的 id。地址故意带 /v1，顺手覆盖「粘贴 SDK base_url」这条路。</summary>
    public async Task<int> CreateChannelAsync(
        string name,
        IEnumerable<string>? upstreamModels = null,
        int priority = 0,
        int weight = 1,
        bool enabled = true,
        string provider = "openai")
    {
        (HttpStatusCode status, JsonElement body) = await PostAsync("/api/channels", new
        {
            name,
            baseUrl = "https://" + name + ".example/v1",
            provider,
            priority,
            weight,
            enabled,
            upstreamModels = upstreamModels?.ToArray(),
        });

        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("channel").GetProperty("id").GetInt32();
    }

    public async Task<JsonElement> AddKeyAsync(int channelId, string secret, string? name = null)
    {
        (HttpStatusCode status, JsonElement body) = await PostAsync(Channel(channelId) + "/keys", new { secret, name });
        Assert.Equal(HttpStatusCode.Created, status);
        return body;
    }

    /// <summary>建一个下发模型（默认未启用，Q35），返回它的 id。</summary>
    public async Task<int> CreateModelAsync(string name, object[] rules, bool enabled = false)
    {
        (HttpStatusCode status, JsonElement body) = await PostAsync("/api/models", new { name, enabled, rules });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("model").GetProperty("id").GetInt32();
    }

    public void Dispose() => Client.Dispose();

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength is null or 0
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>();
}
