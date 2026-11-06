using System.Net;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>价目目录状态接口（Q69/Q71）。测试里关了自动刷新，所以目录一开始是空的。</summary>
public sealed class PriceCatalogApiTests
{
    [Fact]
    public async Task TheCatalogNeedsASession()
    {
        await using var host = new ServerHost();
        using HttpClient browser = host.CreateBrowser();

        HttpResponseMessage response = await browser.GetAsync(new Uri("/api/price-catalog", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BeforeTheFirstDownloadTheCatalogSaysItHasNothingYet()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        JsonElement catalog = await admin.GetJsonAsync("/api/price-catalog");
        JsonElement status = catalog.GetProperty("status");

        Assert.Equal(JsonValueKind.Null, status.GetProperty("fetchedAt").ValueKind);
        Assert.False(status.GetProperty("autoRefresh").GetBoolean());
        Assert.False(status.GetProperty("refreshing").GetBoolean());
        Assert.Equal(0, status.GetProperty("providerCount").GetInt32());
        Assert.Equal(0, catalog.GetProperty("providers").GetArrayLength());
    }
}
