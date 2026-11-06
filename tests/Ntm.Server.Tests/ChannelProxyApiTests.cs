using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;

namespace Ntm.Server.Tests;

/// <summary>渠道代理的管理接口（Q130/Q131）：只写不读、打码显示、不动就保留、解不开就整个渠道退出选路。</summary>
public sealed class ChannelProxyApiTests
{
    private static object Update(string name, string? proxyUrl) => new
    {
        name,
        baseUrl = "https://" + name + ".example/v1",
        provider = "openai",
        proxyUrl,
    };

    private static JsonElement Proxy(JsonElement detail) => detail.GetProperty("channel").GetProperty("proxy");

    [Fact]
    public async Task TheProxyIsWriteOnlyAndShownMasked()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("relay");

        Assert.Equal(JsonValueKind.Null, Proxy(await admin.GetJsonAsync(AdminClient.Channel(id))).ValueKind);

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), Update("relay", "socks5h://user:hunter2@10.0.0.1:1080"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("socks5h://user:***@10.0.0.1:1080", Proxy(saved).GetProperty("masked").GetString());
        Assert.True(Proxy(saved).GetProperty("readable").GetBoolean());

        // 原文不出现在任何响应里，库里存的也是密文。
        string list = (await admin.GetJsonAsync("/api/channels")).GetRawText();
        Assert.DoesNotContain("hunter2", list, StringComparison.Ordinal);
        using (IServiceScope scope = host.Services.CreateScope())
        {
            Channel stored = await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Channels.SingleAsync(c => c.Id == id);
            Assert.NotNull(stored.ProtectedProxyUrl);
            Assert.DoesNotContain("hunter2", stored.ProtectedProxyUrl, StringComparison.Ordinal);
        }

        // 不给 proxyUrl 就不动；快照里拿到的是解开的代理。
        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), Update("relay", null));
        Assert.Equal("socks5h://user:***@10.0.0.1:1080", Proxy(kept).GetProperty("masked").GetString());
        ChannelSnapshot snapshot = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single(c => c.ChannelId == id);
        Assert.Equal("hunter2", snapshot.Proxy?.Password);

        // 空字符串去掉代理。
        (_, JsonElement cleared) = await admin.PutAsync(AdminClient.Channel(id), Update("relay", ""));
        Assert.Equal(JsonValueKind.Null, Proxy(cleared).ValueKind);
        Assert.Null(host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single(c => c.ChannelId == id).Proxy);
    }

    [Fact]
    public async Task AnInvalidProxyIsAFieldError()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("relay");

        (HttpStatusCode status, JsonElement body) = await admin.PutAsync(AdminClient.Channel(id), Update("relay", "ftp://10.0.0.1"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("proxyUrl", out _));
    }

    [Fact]
    public async Task AProxyThatCannotBeDecryptedTakesTheChannelOutOfRoutingInsteadOfGoingDirect()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("relay");
        await admin.AddKeyAsync(id, "sk-relay-1234567890");

        using (IServiceScope scope = host.Services.CreateScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Channel channel = await db.Channels.SingleAsync(c => c.Id == id);
            channel.ProtectedProxyUrl = "not-a-valid-ciphertext";
            await db.SaveChangesAsync();
        }

        ConfigSnapshotProvider snapshots = host.Services.GetRequiredService<ConfigSnapshotProvider>();
        await snapshots.ReloadAsync(CancellationToken.None);

        Assert.Empty(snapshots.Current.Channels.Single(c => c.ChannelId == id).Keys);
        JsonElement proxy = Proxy(await admin.GetJsonAsync(AdminClient.Channel(id)));
        Assert.False(proxy.GetProperty("readable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proxy.GetProperty("masked").ValueKind);
    }
}
