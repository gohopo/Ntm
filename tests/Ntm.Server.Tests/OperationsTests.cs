using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Server.Hosting;

namespace Ntm.Server.Tests;

/// <summary>发布与运维（Q335–Q343）：版本号、健康检查、指标、备份恢复。</summary>
public sealed class OperationsTests
{
    [Fact]
    public async Task HealthzReportsTheVersionAndNamesTheDatabaseThatFailed()
    {
        await using (var host = new ServerHost())
        {
            using HttpClient client = host.CreateBrowser();
            using HttpResponseMessage ok = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("version").GetString()));
            Assert.False(body.RootElement.TryGetProperty("failed", out _));
        }

        await using var broken = new ServerHost(configure: builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IHealthProbe>(new FailingProbe())));
        using HttpClient anonymous = broken.CreateBrowser();
        using HttpResponseMessage failed = await anonymous.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using JsonDocument detail = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", detail.RootElement.GetProperty("status").GetString());
        Assert.Equal(["请求记录库"], detail.RootElement.GetProperty("failed").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(new string[0], null, null, "http://127.0.0.1:5000/healthz")]
    [InlineData(new[] { "--url", "http://10.0.0.5:9000/" }, "http://+:8080", null, "http://10.0.0.5:9000/healthz")]
    [InlineData(new string[0], "http://+:8080;https://+:8443", null, "http://127.0.0.1:8080/healthz")]
    [InlineData(new string[0], "http://[::]:8080", null, "http://[::1]:8080/healthz")]
    [InlineData(new string[0], "http://0.0.0.0:7000", null, "http://127.0.0.1:7000/healthz")]
    [InlineData(new string[0], null, "8080", "http://127.0.0.1:8080/healthz")]
    public void TheHealthcheckCommandFindsTheLocalAddress(string[] args, string? urls, string? ports, string expected)
    {
        Uri? uri = HealthcheckCommand.ResolveUri(args, name => name switch
        {
            "ASPNETCORE_URLS" => urls,
            "ASPNETCORE_HTTP_PORTS" => ports,
            _ => null,
        });

        Assert.Equal(expected, uri?.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task TheHealthcheckCommandExitsByTheStatus(HttpStatusCode status, int expected)
    {
        using var handler = new StubHandler(status);
        using var output = new StringWriter();

        int exit = await HealthcheckCommand.RunAsync([], output, name => name == "ASPNETCORE_HTTP_PORTS" ? "8080" : null, handler);

        Assert.Equal(expected, exit);
        Assert.Equal(new Uri("http://127.0.0.1:8080/healthz"), handler.Requested);
    }

    [Theory]
    [InlineData(null, "0.0.0-dev", null)]
    [InlineData("0.0.0-dev", "0.0.0-dev", null)]
    [InlineData("0.1.0+9fbc013abefa6c2d", "0.1.0", "9fbc013abefa")]
    [InlineData("1.2.0-rc.1+abc", "1.2.0-rc.1", "abc")]
    public void TheVersionComesFromTheInformationalVersion(string? informational, string version, string? commit)
    {
        Assert.Equal((version, commit), BuildInfo.Parse(informational));
    }

    [Fact]
    public async Task TheSettingsPageCanReadTheVersion()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        JsonElement version = await admin.GetJsonAsync("/api/settings/version");

        Assert.Equal(BuildInfo.Version, version.GetProperty("version").GetString());
    }

    [Fact]
    public async Task MetricsAreOffByDefaultAndNeedTheTokenWhenOn()
    {
        await using (var off = new ServerHost())
        {
            using HttpClient client = off.CreateBrowser();
            using HttpResponseMessage response = await client.GetAsync("/metrics");
            Assert.DoesNotContain("ntm_build_info", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await using var host = new ServerHost(settings: new Dictionary<string, string>
        {
            ["Ntm:Metrics:Enabled"] = "true",
            ["Ntm:Metrics:Token"] = "scrape-me",
        });
        using HttpClient anonymous = host.CreateBrowser();
        using (HttpResponseMessage denied = await anonymous.GetAsync("/metrics"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new("Bearer", "scrape-me");
        using HttpResponseMessage allowed = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.StartsWith("text/plain", allowed.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        string text = await allowed.Content.ReadAsStringAsync();
        Assert.Contains("ntm_build_info{version=\"" + BuildInfo.Version + "\"", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE ntm_requests_total counter", text, StringComparison.Ordinal);
        Assert.Contains("ntm_in_flight_requests 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackupTakenWhileRunningRestoresIntoAWorkingGateway()
    {
        string backups = Path.Combine(Path.GetTempPath(), "ntm-backup-" + Guid.NewGuid().ToString("N"));
        string restored = Path.Combine(Path.GetTempPath(), "ntm-restored-" + Guid.NewGuid().ToString("N"));
        try
        {
            string backup = Path.Combine(backups, "one");
            await using (var host = new ServerHost())
            {
                using AdminClient admin = await AdminClient.SignInAsync(host);
                (HttpStatusCode created, _) = await admin.PostAsync("/api/api-keys", new { name = "kept" });
                Assert.Equal(HttpStatusCode.Created, created);

                using var output = new StringWriter();
                int exit = await BackupCommand.RunAsync([backup, "--Ntm:Data:Directory=" + host.DataDirectory], output);
                Assert.True(exit == 0, output.ToString());
                Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
                Assert.True(Directory.Exists(Path.Combine(backup, "keys")));
                Assert.False(File.Exists(Path.Combine(backup, "ntm-debug.db")));

                // 网关还占着数据目录时不让恢复。
                using var refused = new StringWriter();
                Assert.Equal(1, await RestoreCommand.RunAsync([backup, "--Ntm:Data:Directory=" + host.DataDirectory], refused));
                Assert.Contains("先停掉网关", refused.ToString(), StringComparison.Ordinal);
            }

            using (var output = new StringWriter())
            {
                Assert.Equal(0, await RestoreCommand.RunAsync([backup, "--Ntm:Data:Directory=" + restored], output));
            }

            await using var again = new ServerHost(restored);
            using AdminClient back = await AdminClient.SignInAsync(again);
            JsonElement list = await back.GetJsonAsync("/api/api-keys");
            Assert.Contains(list.EnumerateArray(), k => k.GetProperty("name").GetString() == "kept");
        }
        finally
        {
            if (Directory.Exists(backups))
            {
                Directory.Delete(backups, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreKeepsWhatWasThereAside()
    {
        string root = Path.Combine(Path.GetTempPath(), "ntm-aside-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, "backup");
        string data = Path.Combine(root, "data");
        try
        {
            await using (var host = new ServerHost(data))
            {
                // 工厂是懒启动的：碰一下 Services，库才建出来。
                _ = host.Services;
                using var output = new StringWriter();
                Assert.Equal(0, await BackupCommand.RunAsync([backup, "--Ntm:Data:Directory=" + data], output));

                using var occupied = new StringWriter();
                Assert.Equal(1, await BackupCommand.RunAsync([backup, "--Ntm:Data:Directory=" + data], occupied));
            }

            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(data, "ntm.db"), "old");

            using (var output = new StringWriter())
            {
                Assert.Equal(0, await RestoreCommand.RunAsync([backup, "--Ntm:Data:Directory=" + data], output));
            }

            string aside = Assert.Single(Directory.GetDirectories(data, "before-restore-*"));
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(aside, "ntm.db")));
            Assert.NotEqual("old", await File.ReadAllTextAsync(Path.Combine(data, "ntm.db")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FailingProbe : IHealthProbe
    {
        public Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["请求记录库"]);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
