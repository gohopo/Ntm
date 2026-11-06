using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Security;
using Ntm.Server.Hosting;

namespace Ntm.Server.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task WithoutASessionTheApiAnswers401InsteadOfRedirecting()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task AWrongPasswordIsA400AndSetsNoCookie()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { password = "nope" });

        // 不是 401：前端把 401 当成「会话没了」，登录失败不该让它跳来跳去。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("密码不对。", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheRightPasswordSetsAStrictHttpOnlyCookieScopedToTheApi()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        using HttpResponseMessage response = await LoginAsync(client, ServerHost.Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("ntm_session=", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/api", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        JsonElement view = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", view.GetProperty("name").GetString());
    }

    [Fact]
    public async Task LoggingOutEndsTheSession()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await LoginOkAsync(client, ServerHost.Password);

        using HttpResponseMessage logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client));
    }

    [Fact]
    public async Task ChangingThePasswordEndsOtherSessionsButKeepsThisOne()
    {
        await using var host = new ServerHost();
        using HttpClient here = host.CreateBrowser();
        using HttpClient elsewhere = host.CreateBrowser();
        await LoginOkAsync(here, ServerHost.Password);
        await LoginOkAsync(elsewhere, ServerHost.Password);

        using HttpResponseMessage change = await here.PostAsJsonAsync(
            "/api/auth/password",
            new { currentPassword = ServerHost.Password, newPassword = "a-much-longer-password" });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(here));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(elsewhere));

        using HttpClient fresh = host.CreateBrowser();
        await LoginOkAsync(fresh, "a-much-longer-password");
    }

    [Fact]
    public async Task AWrongCurrentPasswordChangesNothing()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await LoginOkAsync(client, ServerHost.Password);

        using HttpResponseMessage change = await client.PostAsJsonAsync(
            "/api/auth/password",
            new { currentPassword = "not-the-password", newPassword = "a-much-longer-password" });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        JsonElement problem = await change.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "当前密码不对。",
            problem.GetProperty("errors").GetProperty("currentPassword")[0].GetString());

        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));
        using HttpClient fresh = host.CreateBrowser();
        await LoginOkAsync(fresh, ServerHost.Password);
    }

    [Fact]
    public async Task ATooShortNewPasswordIsRejected()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await LoginOkAsync(client, ServerHost.Password);

        using HttpResponseMessage change = await client.PostAsJsonAsync(
            "/api/auth/password",
            new { currentPassword = ServerHost.Password, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        JsonElement problem = await change.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            "12",
            problem.GetProperty("errors").GetProperty("newPassword")[0].GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginIsLimitedToFiveAttemptsPerMinute()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage wrong = await LoginAsync(client, "guess-" + attempt.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        // 第六次连正确的密码也不放行：限流在验密码之前。
        using HttpResponseMessage limited = await LoginAsync(client, ServerHost.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task ResettingThePasswordEndsEverySession()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await LoginOkAsync(client, ServerHost.Password);

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminAccountService>().ResetAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client));
    }

    [Fact]
    public async Task TheResetCommandPrintsAWorkingPassword()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ntm-server-tests-" + Guid.NewGuid().ToString("N"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await ResetAdminPasswordCommand.RunAsync(["--Ntm:Data:Directory=" + directory], output);

        Assert.Equal(0, exitCode);
        string printed = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

        // 账号已经由命令建好，启动时不会再读初始密码变量。
        await using var host = new ServerHost(directory);
        using HttpClient client = host.CreateBrowser();
        using (HttpResponseMessage old = await LoginAsync(client, ServerHost.Password))
        {
            Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        }

        await LoginOkAsync(client, printed);
    }

    [Fact]
    public async Task LoginOnlyAcceptsJson()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        using var form = new FormUrlEncodedContent([new("password", ServerHost.Password)]);

        // 只收 JSON：跨站的表单提交连门都进不来（SameSite=Strict 之外的第二道防线）。
        // 内容类型对不上时，路由的 Accepts 策略会落到 /api 的兜底 404，而不是 415；
        // 要紧的是登录处理根本没被调用、没有发 cookie。
        using HttpResponseMessage response = await client.PostAsync("/api/auth/login", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { password });

    private static async Task LoginOkAsync(HttpClient client, string password)
    {
        using HttpResponseMessage response = await LoginAsync(client, password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpStatusCode> MeStatusAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/auth/me");
        return response.StatusCode;
    }
}
