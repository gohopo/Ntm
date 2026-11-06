using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;

namespace Ntm.Server.Tests;

public sealed class ChannelApiTests
{
    private static readonly string[] ReplacedUpstreamModels = ["gpt-4o", "o3-mini"];

    [Fact]
    public async Task ChannelsNeedASession()
    {
        await using var host = new ServerHost();
        using HttpClient anonymous = host.CreateBrowser();

        using HttpResponseMessage response = await anonymous.GetAsync("/api/channels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task APastedSdkBaseUrlIsSplitSoThePathIsNotDoubled()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/channels", new
        {
            name = "openai",
            baseUrl = "https://api.openai.com/v1",
            provider = "openai",
        });

        Assert.Equal(HttpStatusCode.Created, status);
        JsonElement channel = body.GetProperty("channel");
        Assert.Equal("https://api.openai.com", channel.GetProperty("baseUrl").GetString());
        Assert.Null(channel.GetProperty("path").GetString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", channel.GetProperty("requestUrl").GetString());
        Assert.Equal("openAiChat", channel.GetProperty("apiFormat").GetString());
        Assert.Equal(1m, channel.GetProperty("multiplier").GetDecimal());
    }

    [Fact]
    public async Task ANonV1VersionPrefixSurvivesInTheRequestUrl()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (_, JsonElement body) = await admin.PostAsync("/api/channels", new
        {
            name = "ark",
            baseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            provider = "volcengine",
        });

        JsonElement channel = body.GetProperty("channel");
        Assert.Equal(
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
            channel.GetProperty("requestUrl").GetString());
    }

    [Fact]
    public async Task BadInputIsReportedFieldByField()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/channels", new
        {
            name = "   ",
            baseUrl = "not a url",
            weight = 0,
            multiplier = 0m,
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        JsonElement errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("baseUrl", out _));
        Assert.True(errors.TryGetProperty("weight", out _));
        Assert.True(errors.TryGetProperty("multiplier", out _));

        // Provider 没给就是错（Q71）：它决定按谁的价目表计价。
        Assert.True(errors.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task TheProviderIsRequiredButCanBeSaidToBeUnlisted()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        // 「不在列表里」= 空字符串，是一个明确的选择，不是漏填。
        int id = await admin.CreateChannelAsync("relay", provider: "");
        JsonElement detail = await admin.GetJsonAsync(AdminClient.Channel(id));
        Assert.Equal("", detail.GetProperty("channel").GetProperty("provider").GetString());

        // 大小写和空格归一化。
        (HttpStatusCode created, JsonElement body) = await admin.PostAsync("/api/channels", new
        {
            name = "ark",
            baseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            provider = "  VolcEngine  ",
        });
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("volcengine", body.GetProperty("channel").GetProperty("provider").GetString());

        (HttpStatusCode bad, JsonElement badBody) = await admin.PostAsync("/api/channels", new
        {
            name = "bad",
            baseUrl = "https://bad.example/v1",
            provider = "Open AI!",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.True(badBody.GetProperty("errors").TryGetProperty("provider", out _));

        // PUT 不给就保持原值。
        (HttpStatusCode put, JsonElement putBody) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "relay",
            baseUrl = "https://relay.example/v1",
        });
        Assert.Equal(HttpStatusCode.OK, put);
        Assert.Equal("", putBody.GetProperty("channel").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task DuplicateChannelNamesAreRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        await admin.CreateChannelAsync("alpha");

        (HttpStatusCode status, JsonElement body) = await admin.PostAsync("/api/channels", new
        {
            name = "alpha",
            baseUrl = "https://other.example/v1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("已经有同名的渠道了。", body.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task AKeyIsNeverEchoedAndItsPrefixStaysAFragment()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        const string Secret = "sk-abcdefghijklmnopqrstuvwxyz";
        JsonElement key = await admin.AddKeyAsync(id, Secret);

        string prefix = key.GetProperty("prefix").GetString()!;
        Assert.Equal(Secret.Length / 3, prefix.Length);
        Assert.StartsWith(prefix, Secret, StringComparison.Ordinal);
        Assert.NotEqual(Secret, prefix);
        Assert.True(key.GetProperty("readable").GetBoolean());
        Assert.Equal("key-1", key.GetProperty("name").GetString());

        // 整个详情里都不该出现密钥原文。
        (_, JsonElement detail) = await admin.GetAsync(AdminClient.Channel(id));
        Assert.DoesNotContain(Secret, detail.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortSecretIsNotShownWholeInItsPrefix()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("ollama");

        JsonElement key = await admin.AddKeyAsync(id, "ollama12");

        // 自建上游常用很短的密钥；前缀最多露三分之一。
        Assert.Equal("ol", key.GetProperty("prefix").GetString());
    }

    [Fact]
    public async Task TheSameSecretTwiceInOneChannelIsRejectedButSharingAcrossChannelsIsFine()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int first = await admin.CreateChannelAsync("deepseek-openai");
        int second = await admin.CreateChannelAsync("deepseek-anthropic");
        const string Secret = "sk-shared-upstream-secret";
        await admin.AddKeyAsync(first, Secret);

        (HttpStatusCode duplicate, JsonElement body) =
            await admin.PostAsync(AdminClient.Channel(first) + "/keys", new { secret = Secret });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate);
        Assert.Equal("这个密钥已经在这个渠道里了。", body.GetProperty("errors").GetProperty("secret")[0].GetString());

        // Q21：同一份密钥配到另一个渠道是正常用法（同一个账号的两种 API 格式）。
        await admin.AddKeyAsync(second, Secret);
    }

    [Fact]
    public async Task ASecretWithWhitespaceIsRejectedAndABearerPrefixIsStripped()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        (HttpStatusCode status, _) =
            await admin.PostAsync(AdminClient.Channel(id) + "/keys", new { secret = "sk-has space" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        JsonElement key = await admin.AddKeyAsync(id, "Bearer sk-pasted-from-docs");
        Assert.Equal("sk-pas", key.GetProperty("prefix").GetString());
    }

    [Fact]
    public async Task DisablingAKeyTakesItOutOfTheSnapshot()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");
        JsonElement key = await admin.AddKeyAsync(id, "sk-alpha-upstream-secret");
        int keyId = key.GetProperty("id").GetInt32();

        Assert.Single(Snapshot(host).Channels.Single(c => c.ChannelId == id).Keys);

        Assert.Equal(
            HttpStatusCode.OK,
            await admin.PatchAsync(AdminClient.Channel(id) + "/keys/" + keyId, new { enabled = false }));

        Assert.Empty(Snapshot(host).Channels.Single(c => c.ChannelId == id).Keys);

        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync(AdminClient.Channel(id) + "/keys/" + keyId));
        (_, JsonElement detail) = await admin.GetAsync(AdminClient.Channel(id));
        Assert.Empty(detail.GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task DisablingAChannelTakesItOutOfTheSnapshot()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");
        await admin.AddKeyAsync(id, "sk-alpha-upstream-secret");

        Assert.Contains(Snapshot(host).Channels, c => c.ChannelId == id);

        Assert.Equal(HttpStatusCode.NoContent, await admin.PatchAsync(AdminClient.Channel(id), new { enabled = false }));

        Assert.DoesNotContain(Snapshot(host).Channels, c => c.ChannelId == id);
    }

    [Fact]
    public async Task TheUpstreamModelListIsCleanedAndDiffed()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha", ["gpt-4o", " gpt-4o ", "", "gpt-4o-mini"]);

        (_, JsonElement created) = await admin.GetAsync(AdminClient.Channel(id));
        string[] names = [.. created.GetProperty("upstreamModels").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!)];
        Assert.Equal("gpt-4o,gpt-4o-mini", string.Join(',', names.Order(StringComparer.Ordinal)));
        string keptCreatedAt = created.GetProperty("upstreamModels").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "gpt-4o")
            .GetProperty("createdAt").GetRawText();

        // 只给 name 和 baseUrl，upstreamModels 为 null：清单不动。
        (HttpStatusCode untouched, _) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "alpha",
            baseUrl = "https://alpha.example/v1",
        });
        Assert.Equal(HttpStatusCode.OK, untouched);
        (_, JsonElement afterNull) = await admin.GetAsync(AdminClient.Channel(id));
        Assert.Equal(2, afterNull.GetProperty("upstreamModels").GetArrayLength());

        // 给了清单就按差异更新：留下来的保留 createdAt，新的补进去。
        (HttpStatusCode replaced, _) = await admin.PutAsync(AdminClient.Channel(id), new
        {
            name = "alpha",
            baseUrl = "https://alpha.example/v1",
            upstreamModels = ReplacedUpstreamModels,
        });
        Assert.Equal(HttpStatusCode.OK, replaced);

        (_, JsonElement afterDiff) = await admin.GetAsync(AdminClient.Channel(id));
        JsonElement models = afterDiff.GetProperty("upstreamModels");
        Assert.Equal(2, models.GetArrayLength());
        JsonElement kept = models.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "gpt-4o");
        Assert.Equal(keptCreatedAt, kept.GetProperty("createdAt").GetRawText());
        Assert.Contains(models.EnumerateArray(), m => m.GetProperty("name").GetString() == "o3-mini");
        Assert.DoesNotContain(models.EnumerateArray(), m => m.GetProperty("name").GetString() == "gpt-4o-mini");
    }

    [Fact]
    public async Task DeletingAChannelDropsItsScopedRulesAndStripsItFromExclusions()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int alpha = await admin.CreateChannelAsync("alpha", ["gpt-4o"]);
        int beta = await admin.CreateChannelAsync("beta", ["gpt-4o"]);
        await admin.AddKeyAsync(alpha, "sk-alpha-upstream-secret");
        await admin.AddKeyAsync(beta, "sk-beta-upstream-secret");

        int scoped = await admin.CreateModelAsync("only-alpha", [new { kind = "exact", pattern = "gpt-4o", channelId = alpha }]);
        int excluding = await admin.CreateModelAsync(
            "not-alpha",
            [new { kind = "regex", pattern = "gpt-.*", excludedChannelIds = new[] { alpha } }]);

        (_, JsonElement detail) = await admin.GetAsync(AdminClient.Channel(alpha));
        JsonElement scopedRules = detail.GetProperty("scopedRules");
        Assert.Equal("only-alpha", Assert.Single(scopedRules.EnumerateArray()).GetProperty("publishedModel").GetString());

        Assert.Equal(HttpStatusCode.NoContent, await admin.DeleteAsync(AdminClient.Channel(alpha)));

        // 作用在这个渠道上的规则随渠道一起消失。
        (_, JsonElement afterScoped) = await admin.GetAsync(AdminClient.Model(scoped));
        Assert.Empty(afterScoped.GetProperty("rules").EnumerateArray());

        // 排除名单里的 id 要清掉，否则下次编辑会被校验挡住。
        (_, JsonElement afterExcluding) = await admin.GetAsync(AdminClient.Model(excluding));
        JsonElement rule = Assert.Single(afterExcluding.GetProperty("rules").EnumerateArray());
        Assert.Empty(rule.GetProperty("excludedChannelIds").EnumerateArray());
    }

    [Fact]
    public async Task AnUnknownChannelIs404()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);

        (HttpStatusCode status, JsonElement body) = await admin.GetAsync(AdminClient.Channel(9999));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("渠道不存在。", body.GetProperty("title").GetString());
    }

    private static ConfigSnapshot Snapshot(ServerHost host) =>
        host.Services.GetRequiredService<ConfigSnapshotProvider>().Current;
}
