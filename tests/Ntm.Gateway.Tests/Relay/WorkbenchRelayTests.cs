using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// Workbench 的请求（Q200、Q208–Q210）：固定到渠道时不选路、不受限制、失败不写冷却和健康度、成功清冷却；
/// 以下发模型为目标时照常选路，但跳过冷却中的候选、也不写冷却。
/// </summary>
public sealed class WorkbenchRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Done = "data: [DONE]\n\n";

    private static HttpResponseMessage Ok() => ScriptedUpstream.Sse(Content, Finish, Done);

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    private static HttpResponseMessage Unauthorized() =>
        ScriptedUpstream.Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");

    private static string Body(string model) =>
        $"{{\"model\":\"{model}\",\"stream\":true,\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}]}}";

    private static async Task<int> ChannelIdAsync(RelayHost host, string name)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        return await db.Channels.Where(c => c.Name == name).Select(c => c.Id).SingleAsync();
    }

    [Fact]
    public async Task APinnedSendSkipsRoutingAndIsRecordedAsWorkbench()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a"]), ("b", 0, ["sk-b"]));
        host.Upstream.Script("sk-a", Ok);
        host.Upstream.Script("sk-b", Ok);
        int b = await ChannelIdAsync(host, "b");

        // 上游模型不必在渠道的模型列表里：Admin 常常要先试一个还没同步的名字。
        using HttpResponseMessage response = await host.PostWorkbenchAsync($"/workbench/channels/{b}/send", Body("not-synced"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("x-ntm-request-id"));
        Assert.Equal(["sk-b"], host.Upstream.SecretsTried);
        Assert.Contains("\"model\":\"not-synced\"", host.Upstream.Calls.Single().Body, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestOrigin.Workbench, record.Origin);
        Assert.Null(record.ApiKeyId);
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(b, attempt.ChannelId);
    }

    [Fact]
    public async Task ClientRequestsAreRecordedAsClient()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(Body("gpt-4o"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestOrigin.Client, record.Origin);
        Assert.NotNull(record.ApiKeyId);
    }

    [Fact]
    public async Task APinnedFailureWritesNeitherCooldownNorHealth()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        host.Upstream.Script("sk-a", ServerError);
        int a = await ChannelIdAsync(host, "a");

        using HttpResponseMessage response = await host.PostWorkbenchAsync($"/workbench/channels/{a}/send", Body("gpt-4o"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
        Assert.Equal(1, host.Services.GetRequiredService<HealthTracker>().Factor(a, "gpt-4o"));

        AttemptRecord attempt = Assert.Single(Assert.Single(await host.ReadRecordsAsync()).Attempts);
        Assert.Equal(FailureClass.UpstreamServerError, attempt.FailureClass);
        Assert.Equal(CooldownScope.None, attempt.CooldownScope);
        Assert.Null(attempt.CooldownUntil);
    }

    [Fact]
    public async Task APinnedSendGoesThroughACoolingKeyAndASuccessClearsTheCooldown()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-a"]));
        int a = await ChannelIdAsync(host, "a");
        CooldownRegistry cooldowns = host.Services.GetRequiredService<CooldownRegistry>();

        // 先让客户端的请求把这把密钥打进冷却（认证失败冷却密钥）。
        host.Upstream.Script("sk-a", Unauthorized);
        using (HttpResponseMessage failed = await host.PostAsync(Body("gpt-4o")))
        {
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        }

        Assert.Contains(cooldowns.Active(), e => e.Key.Scope == CooldownScope.Key);

        // 换好密钥之后在 Workbench 里点一下：冷却中照样发，成功就把冷却清掉。
        host.Upstream.Script("sk-a", Ok);
        using HttpResponseMessage response = await host.PostWorkbenchAsync($"/workbench/channels/{a}/send", Body("gpt-4o"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(cooldowns.Active());

        using HttpResponseMessage client = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.OK, client.StatusCode);
    }

    [Fact]
    public async Task APinnedSendTriesTheNextKeyOnlyForKeyScopedFailures()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-1", "sk-2", "sk-3"]));
        host.Upstream.Script("sk-1", Unauthorized);
        host.Upstream.Script("sk-2", ServerError);
        host.Upstream.Script("sk-3", Ok);
        int a = await ChannelIdAsync(host, "a");

        using HttpResponseMessage response = await host.PostWorkbenchAsync($"/workbench/channels/{a}/send", Body("gpt-4o"));

        // 认证失败换下一把；服务端错误跟密钥无关，到此为止。
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["sk-1", "sk-2"], host.Upstream.SecretsTried);
        Assert.Empty(host.Services.GetRequiredService<CooldownRegistry>().Active());
    }

    [Fact]
    public async Task DisabledChannelsAndKeysCanBeTestedWhenChosenExplicitly()
    {
        int keyId = 0;
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("off", 0, ["sk-off"])],
            (db, channels) =>
            {
                channels[0].Enabled = false;
                channels[0].Keys.Single().Enabled = false;
            });
        host.Upstream.Script("sk-off", Ok);
        int channelId = await ChannelIdAsync(host, "off");

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            keyId = await db.ChannelKeys.Where(k => k.ChannelId == channelId).Select(k => k.Id).SingleAsync();
        }

        // 不指定密钥：只用启用的，没有就是 503（不是 401/403）。
        using HttpResponseMessage auto = await host.PostWorkbenchAsync($"/workbench/channels/{channelId}/send", Body("x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, auto.StatusCode);
        Assert.Empty(host.Upstream.Calls);

        using HttpResponseMessage chosen = await host.PostWorkbenchAsync(
            $"/workbench/channels/{channelId}/send?key={keyId}",
            Body("x"));
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(["sk-off"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task APinnedSendIgnoresChannelRestrictionsButStillCountsCost()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                // 一周里一分钟都不开放，而且每把密钥只许 gpt-4o。
                channels[0].TimeWindows.Add(new ChannelTimeWindow { Weekdays = 0, StartMinute = 0, EndMinute = 1 });
                channels[0].Keys.Single().AllowedModels = ["gpt-4o"];
                channels[0].UpstreamModels.Add(new UpstreamModel { Name = "gpt-4o", Source = UpstreamModelSource.Manual });
            });
        host.Upstream.Script("sk-a", Ok);
        int a = await ChannelIdAsync(host, "a");

        using HttpResponseMessage response = await host.PostWorkbenchAsync($"/workbench/channels/{a}/send", Body("other-model"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Upstream.Calls);
    }

    [Fact]
    public async Task ModelSendsRouteNormallyButSkipCoolingCandidatesAndWriteNoCooldown()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 10, ["sk-a"]), ("b", 0, ["sk-b"]));
        int b = await ChannelIdAsync(host, "b");
        CooldownRegistry cooldowns = host.Services.GetRequiredService<CooldownRegistry>();

        // 客户端的请求把 a 的密钥打进冷却，然后落到 b。
        host.Upstream.Script("sk-a", Unauthorized);
        host.Upstream.Script("sk-b", Ok);
        using (HttpResponseMessage client = await host.PostAsync(Body("gpt-4o")))
        {
            Assert.Equal(HttpStatusCode.OK, client.StatusCode);
        }

        int before = cooldowns.Active().Count;
        host.Upstream.Script("sk-b", ServerError);

        using HttpResponseMessage response = await host.PostWorkbenchAsync("/workbench/models/send", Body("gpt-4o"));

        // a 在冷却，不碰；b 失败了也不进冷却、不扣健康度。
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["sk-a", "sk-b", "sk-b"], host.Upstream.SecretsTried);
        Assert.Equal(before, cooldowns.Active().Count);
        Assert.Equal(1, host.Services.GetRequiredService<HealthTracker>().Factor(b, "gpt-4o"));

        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(RequestOrigin.Workbench, records[^1].Origin);
    }

    [Fact]
    public async Task AnthropicWebSearchRoutesAroundOtherFormats()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("chat", 10, ["sk-chat"], ApiFormat.OpenAiChat),
            ("claude", 0, ["sk-claude"], ApiFormat.Anthropic));
        host.Upstream.Script("sk-claude", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-x\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"));

        // 第二轮：历史里带着上一轮的搜索块，工具里有服务端的 web_search。
        const string body = """
            {"model":"claude-x","max_tokens":64,
             "tools":[{"type":"web_search_20250305","name":"web_search","max_uses":5}],
             "messages":[
               {"role":"user","content":"news?"},
               {"role":"assistant","content":[
                 {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"news"}},
                 {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[]},
                 {"type":"text","text":"here"}]},
               {"role":"user","content":"more"}]}
            """;

        using HttpResponseMessage response = await host.PostMessagesAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-claude"], host.Upstream.SecretsTried);
        Assert.Contains("web_search_tool_result", host.Upstream.Calls.Single().Body, StringComparison.Ordinal);
    }
}
