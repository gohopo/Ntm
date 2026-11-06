using System.Net;
using System.Text;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>/api/workbench（Q200–Q202）：管理员登录态鉴权，出错时不冒充登录失效。</summary>
public sealed class WorkbenchApiTests
{
    private const string Chat = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}]}""";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task WorkbenchNeedsTheAdminSession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage channel = await anonymous.PostAsync("/api/workbench/channels/1/send", Json(Chat));
        using HttpResponseMessage model = await anonymous.PostAsync("/api/workbench/models/send", Json(Chat));

        Assert.Equal(HttpStatusCode.Unauthorized, channel.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, model.StatusCode);
    }

    [Fact]
    public async Task UnknownChannelsKeysAndFormatsAreRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("a", ["gpt-4o"]);

        using HttpResponseMessage missing = await admin.Client.PostAsync("/api/workbench/channels/999/send", Json(Chat));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using HttpResponseMessage missingKey = await admin.Client.PostAsync(
            $"/api/workbench/channels/{channel}/send?key=999",
            Json(Chat));
        Assert.Equal(HttpStatusCode.NotFound, missingKey.StatusCode);

        using HttpResponseMessage format = await admin.Client.PostAsync("/api/workbench/models/send?format=gemini", Json(Chat));
        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
    }

    [Fact]
    public async Task GatewayErrorsCarryTheRequestIdAndAreNeverUnauthorized()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int channel = await admin.CreateChannelAsync("a", ["gpt-4o"]);

        // 渠道没有密钥：503，响应头带请求 ID，页面据此打开请求详情。
        using HttpResponseMessage noKeys = await admin.Client.PostAsync($"/api/workbench/channels/{channel}/send", Json(Chat));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noKeys.StatusCode);
        Assert.True(noKeys.Headers.Contains("x-ntm-request-id"));

        // 没有这个下发模型：按 OpenAI 的样子报 404，同样带请求 ID。
        using HttpResponseMessage noModel = await admin.Client.PostAsync("/api/workbench/models/send", Json(Chat));
        Assert.Equal(HttpStatusCode.NotFound, noModel.StatusCode);
        Assert.True(noModel.Headers.Contains("x-ntm-request-id"));
        using JsonDocument error = JsonDocument.Parse(await noModel.Content.ReadAsStringAsync());
        Assert.True(error.RootElement.TryGetProperty("error", out _));

        // Anthropic 格式的回复也是 Anthropic 的样子。
        using HttpResponseMessage anthropic = await admin.Client.PostAsync(
            "/api/workbench/models/send?format=anthropic",
            Json("""{"model":"gpt-4o","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(HttpStatusCode.NotFound, anthropic.StatusCode);
        using JsonDocument anthropicError = JsonDocument.Parse(await anthropic.Content.ReadAsStringAsync());
        Assert.Equal("error", anthropicError.RootElement.GetProperty("type").GetString());
    }
}
