using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;

namespace Ntm.Server.Tests;

/// <summary>Anthropic 渠道的认证头选项（Q166）：默认 x-api-key，可改成 Bearer，不给就不动。</summary>
public sealed class AnthropicChannelApiTests
{
    [Fact]
    public async Task TheBearerOptionDefaultsOffCanBeTurnedOnAndIsKeptWhenOmitted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("claude");

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        Assert.False(detail.GetProperty("channel").GetProperty("anthropicBearerAuth").GetBoolean());

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
            anthropicBearerAuth = true,
        });
        Assert.True(status == HttpStatusCode.OK, saved.ToString());
        Assert.True(saved.GetProperty("channel").GetProperty("anthropicBearerAuth").GetBoolean());

        ChannelSnapshot channel = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels
            .Single(c => c.ChannelId == id);
        Assert.True(channel.AnthropicBearerAuth);

        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
        });
        Assert.True(kept.GetProperty("channel").GetProperty("anthropicBearerAuth").GetBoolean());
    }

    [Fact]
    public async Task TheAutoCacheBreakpointsSwitchDefaultsOnCanBeTurnedOffAndIsKeptWhenOmitted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("claude");

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        Assert.True(detail.GetProperty("channel").GetProperty("autoCacheBreakpoints").GetBoolean());

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
            autoCacheBreakpoints = false,
        });
        Assert.True(status == HttpStatusCode.OK, saved.ToString());
        Assert.False(saved.GetProperty("channel").GetProperty("autoCacheBreakpoints").GetBoolean());
        Assert.False(host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels
            .Single(c => c.ChannelId == id).AutoCacheBreakpoints);

        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
        });
        Assert.False(kept.GetProperty("channel").GetProperty("autoCacheBreakpoints").GetBoolean());
    }

    [Fact]
    public async Task TheKeysShareAccountSwitchDefaultsOffCanBeTurnedOnAndIsKeptWhenOmitted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("claude");

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        Assert.False(detail.GetProperty("channel").GetProperty("keysShareAccount").GetBoolean());

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
            keysShareAccount = true,
        });
        Assert.True(status == HttpStatusCode.OK, saved.ToString());
        Assert.True(saved.GetProperty("channel").GetProperty("keysShareAccount").GetBoolean());
        Assert.True(host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels
            .Single(c => c.ChannelId == id).KeysShareAccount);

        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
        });
        Assert.True(kept.GetProperty("channel").GetProperty("keysShareAccount").GetBoolean());
    }

    [Fact]
    public async Task TheThinkingStyleDefaultsToAutoCanBeChangedAndIsKeptWhenOmitted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("claude");

        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        Assert.Equal("auto", detail.GetProperty("channel").GetProperty("thinkingStyle").GetString());

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
            thinkingStyle = "adaptive",
        });
        Assert.True(status == HttpStatusCode.OK, saved.ToString());
        Assert.Equal("adaptive", saved.GetProperty("channel").GetProperty("thinkingStyle").GetString());
        Assert.Equal(Ntm.Protocol.Anthropic.AnthropicThinkingStyle.Adaptive, host.Services.GetRequiredService<ConfigSnapshotProvider>()
            .Current.Channels.Single(c => c.ChannelId == id).ThinkingStyle);

        (_, JsonElement kept) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "claude",
            baseUrl = "https://claude.example",
            apiFormat = "anthropic",
            provider = "anthropic",
        });
        Assert.Equal("adaptive", kept.GetProperty("channel").GetProperty("thinkingStyle").GetString());
    }
}
