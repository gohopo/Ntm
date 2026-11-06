using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Ntm.Gateway.Security;

namespace Ntm.Server.Tests;

/// <summary>每个测试一个独立的 Ntm：自己的临时数据目录、固定的管理员初始密码。</summary>
internal sealed class ServerHost : WebApplicationFactory<Program>
{
    public const string Password = "correct-horse-battery";

    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly Action<IWebHostBuilder>? _configure;

    public ServerHost(
        string? dataDirectory = null,
        IReadOnlyDictionary<string, string>? settings = null,
        Action<IWebHostBuilder>? configure = null)
    {
        _settings = settings ?? new Dictionary<string, string>();
        _configure = configure;
        DataDirectory = dataDirectory
            ?? Path.Combine(Path.GetTempPath(), "ntm-server-tests-" + Guid.NewGuid().ToString("N"));
    }

    public string DataDirectory { get; }

    /// <summary>像浏览器一样带 cookie，但不跟随重定向——要能看到接口有没有乱跳。</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Ntm:Data:Directory", DataDirectory);
        builder.UseSetting(AdminAccountService.InitialPasswordVariable, Password);

        // 测试不许连外网：关掉价目目录的自动刷新（Q69 的 0 = 关闭）。
        builder.UseSetting("Ntm:Pricing:RefreshIntervalHours", "0");
        builder.UseSetting("Ntm:ModelSync:IntervalMinutes", "0");

        foreach ((string key, string value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        _configure?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
    }
}
