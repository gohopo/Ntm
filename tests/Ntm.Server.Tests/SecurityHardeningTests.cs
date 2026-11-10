using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;
using Ntm.Gateway.Security;
using Ntm.Server.Admin;

namespace Ntm.Server.Tests;

public sealed class SecurityHardeningTests
{
    private static readonly int[] AuditWeekdays = [1, 2];
    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("none", true)]
    [InlineData("same-site", false)]
    [InlineData("cross-site", false)]
    public async Task FetchMetadataControlsBodylessMutations(string site, bool allowed)
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await SignInAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost", true)]
    [InlineData("https://localhost", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost:1234", false)]
    [InlineData("null", false)]
    [InlineData("ftp://localhost", false)]
    public async Task OriginFallbackAndHeaderlessScripts(string? origin, bool allowed)
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password = ServerHost.Password }),
        };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WorkbenchRawBodyAlsoRequiresJson()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await SignInAsync(client);
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.PostAsync("/api/workbench/models/send", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task ResetPasswordOnlyUnlocksAfterChangingToADifferentPassword()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        _ = host.Services;
        string generated;
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            generated = await scope.ServiceProvider.GetRequiredService<AdminAccountService>().ResetAsync();
        }

        using HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new { password = generated });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        JsonElement me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());
        foreach (string path in new[] { "/api/channels", "/api/settings/version", "/api/audit" })
        {
            using HttpResponseMessage blocked = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            JsonElement problem = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("must-change-password", problem.GetProperty("code").GetString());
        }

        using HttpResponseMessage unchanged = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = generated, newPassword = generated });
        Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
        using HttpResponseMessage changed = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = generated, newPassword = "private-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using HttpResponseMessage allowed = await client.GetAsync("/api/channels");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.False(me.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task LogoutAllRevokesBothDevicesWithoutChangingPassword()
    {
        await using var host = new ServerHost();
        using HttpClient first = host.CreateBrowser();
        using HttpClient second = host.CreateBrowser();
        await SignInAsync(first);
        await SignInAsync(second);
        using HttpResponseMessage logout = await first.PostAsync("/api/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        foreach (HttpClient client in new[] { first, second })
        {
            using HttpResponseMessage me = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        await SignInAsync(second);
    }

    [Theory]
    [InlineData(-29, true)]
    [InlineData(-30, false)]
    [InlineData(-31, false)]
    [InlineData(1, false)]
    public async Task AbsoluteLifetimeCannotBeExtendedByARenewedTicket(int ageDays, bool allowed)
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        CookieAuthenticationOptions options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminAuthentication.Scheme);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "admin"),
            new Claim("ntm:sv", "1"),
            new Claim("ntm:iat", now.AddDays(ageDays).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        ], AdminAuthentication.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = now.AddDays(7),
        }, AdminAuthentication.Scheme);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", AdminAuthentication.CookieName + "=" + options.TicketDataFormat.Protect(ticket));
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ThrottleSharesIpv6Slash64AndCountsGlobalFailures()
    {
        var clock = new TestClock();
        using var throttle = new LoginThrottle(clock);
        IPAddress first = IPAddress.Parse("2001:db8:1:1::1");
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(throttle.RetryAfter(first));
            throttle.RecordFailure(first);
        }

        Assert.NotNull(throttle.RetryAfter(IPAddress.Parse("2001:db8:1:1::abcd")));
        Assert.Null(throttle.RetryAfter(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.Equal(LoginThrottle.PartitionKey(IPAddress.Parse("::ffff:192.0.2.1")), LoginThrottle.PartitionKey(IPAddress.Parse("192.0.2.1")));
        for (int i = 0; i < 25; i++)
        {
            throttle.RecordFailure(IPAddress.Parse("192.0.2." + (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        Assert.NotNull(throttle.RetryAfter(IPAddress.Parse("198.51.100.1")));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Null(throttle.RetryAfter(first));
    }

    [Fact]
    public async Task SuccessfulLoginsDoNotConsumeFailureBudget()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        for (int i = 0; i < 7; i++)
        {
            await SignInAsync(client);
        }
    }

    [Fact]
    public async Task NewInstallHasNoDefaultApiKeyAndExpiryCanBeSetAndCleared()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await SignInAsync(client);
        JsonElement keys = await client.GetFromJsonAsync<JsonElement>("/api/api-keys");
        Assert.Empty(keys.EnumerateArray());
        string expiry = DateTimeOffset.UtcNow.AddDays(3).ToString("O", CultureInfo.InvariantCulture);
        using HttpResponseMessage create = await client.PostAsJsonAsync("/api/api-keys", new { name = "temporary", expiresAt = expiry });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        JsonElement created = await create.Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("view").GetProperty("id").GetInt32();
        Assert.Equal(DateTimeOffset.Parse(expiry, CultureInfo.InvariantCulture), created.GetProperty("view").GetProperty("expiresAt").GetDateTimeOffset());
        string path = "/api/api-keys/" + id.ToString(CultureInfo.InvariantCulture);
        using HttpResponseMessage rename = await client.PatchAsJsonAsync(path, new { name = "renamed" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("apiKey").GetProperty("expiresAt").ValueKind);
        using HttpResponseMessage clear = await client.PatchAsJsonAsync(path, new { expiresAt = "" });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        detail = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("apiKey").GetProperty("expiresAt").ValueKind);
        using HttpResponseMessage past = await client.PatchAsJsonAsync(path, new { expiresAt = "2000-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
    }

    [Fact]
    public async Task AuditTracksMutationFieldsWithoutSecretValuesAndSupportsPaging()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        await SignInAsync(client);
        using HttpResponseMessage create = await client.PostAsJsonAsync("/api/api-keys", new { name = "audit-key" });
        create.EnsureSuccessStatusCode();
        JsonElement key = await create.Content.ReadFromJsonAsync<JsonElement>();
        int id = key.GetProperty("view").GetProperty("id").GetInt32();
        string secret = key.GetProperty("secret").GetString()!;
        using HttpResponseMessage patch = await client.PatchAsJsonAsync("/api/api-keys/" + id.ToString(CultureInfo.InvariantCulture), new { enabled = false });
        patch.EnsureSuccessStatusCode();
        JsonElement page = await client.GetFromJsonAsync<JsonElement>("/api/audit?limit=1");
        JsonElement entry = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("PATCH /api/api-keys/{id}", entry.GetProperty("action").GetString());
        Assert.Equal("audit-key", entry.GetProperty("targetName").GetString());
        JsonElement change = Assert.Single(entry.GetProperty("changes").EnumerateArray());
        Assert.Contains("Enabled", change.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain(secret, page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fingerprint", page.GetRawText(), StringComparison.Ordinal);
        string cursor = page.GetProperty("nextCursor").GetString()!;
        JsonElement next = await client.GetFromJsonAsync<JsonElement>("/api/audit?limit=1&cursor=" + cursor);
        Assert.True(next.GetProperty("items")[0].GetProperty("id").GetInt64() < entry.GetProperty("id").GetInt64());
        JsonElement logins = await client.GetFromJsonAsync<JsonElement>("/api/audit?action=auth.login");
        Assert.Single(logins.GetProperty("items").EnumerateArray());
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        string persisted = JsonSerializer.Serialize(await traffic.AuditEntries.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerHost.Password, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFetchMetadataIsRejectedWhenTheTransportPreservesIt()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Sec-Fetch-Site"] = "";
        Assert.False(CrossSiteGuard.IsSameOrigin(context.Request, out _));
    }

    [Fact]
    public async Task ParallelGuessesCannotRacePastTheFailureBudget()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => client.PostAsJsonAsync("/api/auth/login", new { password = "wrong" })));
        try
        {
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Http2BodyWithoutContentLengthCannotBypassJsonGuard()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseNtmCrossSiteGuard();
        app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/api/workbench/models/send";
        context.Request.Method = "POST";
        context.Request.Protocol = "HTTP/2";
        context.Request.ContentType = "text/plain";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        await app.Build()(context);
        Assert.Equal(415, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProxyAuditContainsOnlyFieldAliasAndDeletedTargetNameSurvives()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("audit-channel");
        (HttpStatusCode status, _) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "audit-channel",
            baseUrl = "https://upstream.example/v1",
            provider = "openai",
            proxyUrl = "socks5h://private-user:private-secret@10.0.0.1:1080",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement page = await admin.GetJsonAsync("/api/audit?limit=1");
        JsonElement entry = page.GetProperty("items")[0];
        Assert.Contains("Proxy", entry.GetProperty("changes")[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("private-secret", page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", page.GetRawText(), StringComparison.Ordinal);
        await admin.DeleteAsync(AdminClient.Channel(id));
        page = await admin.GetJsonAsync("/api/audit?limit=1");
        entry = page.GetProperty("items")[0];
        Assert.Equal("audit-channel", entry.GetProperty("targetName").GetString());
        Assert.Equal("deleted", entry.GetProperty("changes")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ForwardingHeadersWithoutTrustSurfaceWarningButCannotSpoofAuditIp()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateBrowser();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.15");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        await SignInAsync(client);
        JsonElement warnings = await client.GetFromJsonAsync<JsonElement>("/api/settings/warnings");
        Assert.True(warnings.GetProperty("untrustedForwardedHeaders").GetBoolean());
        JsonElement page = await client.GetFromJsonAsync<JsonElement>("/api/audit?action=auth.login");
        Assert.NotEqual("198.51.100.15", page.GetProperty("items")[0].GetProperty("remoteAddress").GetString());
    }

    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    [Theory]
    [InlineData("https", "ntm.example", "https://ntm.example", true)]
    [InlineData("https", "ntm.example", "http://ntm.example", false)]
    [InlineData("http", "ntm.example", "https://ntm.example", false)]
    [InlineData("https", "ntm.example:8443", "https://ntm.example:8443", true)]
    [InlineData("https", "ntm.example:8443", "https://ntm.example", false)]
    public void OriginFallbackMatchesEffectivePort(string scheme, string host, string origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;
        Assert.Equal(allowed, CrossSiteGuard.IsSameOrigin(context.Request, out _));
    }

    [Fact]
    public async Task OwnedJsonChangesRecordOnlyOwnerFieldNames()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("window-audit");
        (HttpStatusCode status, _) = await admin.PutAsync(AdminClient.Channel(id) + "/restrictions", new
        {
            timeWindows = new[] { new { weekdays = AuditWeekdays, start = "09:00", end = "18:00" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement page = await admin.GetJsonAsync("/api/audit?limit=1");
        Assert.Contains(page.GetProperty("items")[0].GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("entity").GetString() == "Channel"
                && c.GetProperty("fields").EnumerateArray().Any(f => f.GetString() == "TimeWindows"));
        Assert.DoesNotContain("09:00", page.GetRawText(), StringComparison.Ordinal);
        (status, _) = await admin.PutAsync(AdminClient.Channel(id) + "/restrictions", new { timeWindows = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, status);
        page = await admin.GetJsonAsync("/api/audit?limit=1");
        Assert.Contains(page.GetProperty("items")[0].GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("entity").GetString() == "Channel"
                && c.GetProperty("fields").EnumerateArray().Any(f => f.GetString() == "TimeWindows"));
    }

    [Fact]
    public async Task ChannelKeyAuditUsesTheKeyTargetRatherThanParentChannel()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("parent-channel");
        JsonElement key = await admin.AddKeyAsync(id, "sk-private-upstream-token");
        int keyId = key.GetProperty("id").GetInt32();
        await admin.PatchAsync(AdminClient.Channel(id) + "/keys/" + keyId.ToString(CultureInfo.InvariantCulture), new { name = "named-key" });
        JsonElement page = await admin.GetJsonAsync("/api/audit?limit=1");
        JsonElement entry = page.GetProperty("items")[0];
        Assert.Equal("channel-keys", entry.GetProperty("targetType").GetString());
        Assert.Equal(keyId.ToString(CultureInfo.InvariantCulture), entry.GetProperty("targetId").GetString());
        Assert.Equal("named-key", entry.GetProperty("targetName").GetString());
        Assert.DoesNotContain("sk-private-upstream-token", page.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { password = ServerHost.Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
