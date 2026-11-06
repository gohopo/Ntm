using System.Net;
using System.Text;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>管理接口的请求体绑定失败回带字段名的 400，开发和生产环境一致（Q304）。</summary>
public sealed class BadRequestApiTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(AdminClient admin, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await admin.Client.PostAsync(url, content);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task AnUnknownEnumValueNamesTheFieldAndTheAllowedValues()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await PostRawAsync(admin, "/api/channels",
            "{\"name\":\"x\",\"baseUrl\":\"https://x.example\",\"apiFormat\":\"gemini\",\"provider\":\"\"}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("提交的内容有问题。", body.GetProperty("title").GetString());
        string message = Assert.Single(body.GetProperty("errors").GetProperty("apiFormat").EnumerateArray()).GetString()!;
        Assert.Contains("openAiChat", message, StringComparison.Ordinal);
        Assert.Contains("anthropic", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedFieldsAndWrongTypesAreNamedToo()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await PostRawAsync(admin, "/api/models",
            "{\"name\":\"m\",\"enabled\":true,\"rules\":[{\"kind\":\"fuzzy\",\"pattern\":\"m\"}]}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("rules[0].kind", out _));

        (status, body) = await PostRawAsync(admin, "/api/channels",
            "{\"name\":\"x\",\"baseUrl\":\"https://x.example\",\"priority\":\"high\",\"provider\":\"\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("类型不对。", Assert.Single(body.GetProperty("errors").GetProperty("priority").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task BrokenJsonIsAWholeRequestError()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await PostRawAsync(admin, "/api/channels", "{\"name\":\"x\",");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("请求体不是合法的 JSON。", Assert.Single(body.GetProperty("errors").GetProperty("").EnumerateArray()).GetString());
    }
}
