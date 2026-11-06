using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 切片 3 的选路：PublishedModel 决定能不能进来、MatchRule 决定落到哪个
/// （Channel, UpstreamModel），冷却按作用域生效（Q28、Q29、Q36–Q38）。
/// </summary>
public sealed class RoutingRelayTests
{
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n";
    private const string Finish = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Done = "data: [DONE]\n\n";

    private static HttpResponseMessage Ok() => ScriptedUpstream.Sse(Content, Finish, Done);

    private static HttpResponseMessage ServerError() =>
        ScriptedUpstream.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}");

    private static HttpResponseMessage Unauthorized() =>
        ScriptedUpstream.Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");

    private static string Body(string model, bool stream = true) =>
        "{\"model\":\"" + model + "\",\"stream\":" + (stream ? "true" : "false") + ",\"messages\":[]}";

    /// <summary>把一条「所有渠道 + 精确名」的规则挂到一个已启用的下发模型上。</summary>
    private static void Publish(ConfigDbContext db, string published, string pattern, MatchKind kind = MatchKind.Exact)
    {
        var model = new PublishedModel { Name = published, Enabled = true };
        model.Rules.Add(new MatchRule { Ordinal = 1, Kind = kind, Pattern = pattern });
        db.PublishedModels.Add(model);
    }

    private static void Offer(Channel channel, params string[] names)
    {
        foreach (string name in names)
        {
            channel.UpstreamModels.Add(new UpstreamModel { Name = name, Source = UpstreamModelSource.Manual });
        }
    }

    [Fact]
    public async Task ThePublishedNameIsRewrittenToTheUpstreamName()
    {
        // 下发名 fast，上游真名 gpt-4o-mini：请求体里的 model 必须被换掉，响应里的名字不改（ADR-0002）。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o-mini");
                Publish(db, "fast", "gpt-4o-mini");
            });

        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(Body("fast"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", host.Upstream.Calls.Single().Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\":\"fast\"", host.Upstream.Calls.Single().Body, StringComparison.Ordinal);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal("fast", record.PublishedModel);
        Assert.Equal("gpt-4o-mini", Assert.Single(record.Attempts).UpstreamModel);
    }

    [Fact]
    public async Task AnUnpublishedModelIsRefusedWithoutTouchingAnyUpstream()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        host.Upstream.Script("sk-a", Ok);

        using HttpResponseMessage response = await host.PostAsync(Body("secret-model"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("model_not_found", payload, StringComparison.Ordinal);
        Assert.Contains("secret-model", payload, StringComparison.Ordinal);
        Assert.Empty(host.Upstream.Calls);

        // 认证过的请求即使没下发也要记一条，否则面板上看不到「谁在要什么模型」。
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Failed, record.Status);
        Assert.Equal(404, record.HttpStatusCode);
        Assert.Empty(record.Attempts);
    }

    [Fact]
    public async Task AnUnpublishedModelIsRefusedInTheAnthropicShape()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        using HttpResponseMessage response = await host.PostMessagesAsync(
            "{\"model\":\"nope\",\"max_tokens\":16,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ARegexRuleMatchesEveryUpstreamModelTheChannelOffers()
    {
        // 一条正则规则可以同时覆盖多个上游模型，命中顺序确定（优先级 → 名字字典序）。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "claude-sonnet-4-5", "claude-haiku-4-5", "gpt-4o");
                Publish(db, "claude", "claude-.*", MatchKind.Regex);
            });

        host.Upstream.Script("sk-a", ServerError);

        using HttpResponseMessage response = await host.PostAsync(Body("claude"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // 两个 claude-* 都被试过，gpt-4o 没有；字典序决定 haiku 在前。
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(
            ["claude-haiku-4-5", "claude-sonnet-4-5"],
            record.Attempts.Select(a => a.UpstreamModel));
    }

    [Fact]
    public async Task AHigherPriorityChannelIsUsedFirstAndTheLowerOneCatchesTheFailure()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("primary", 10, ["sk-p"]), ("backup", 0, ["sk-b"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Offer(channels[1], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        host.Upstream.Script("sk-p", ServerError);
        host.Upstream.Script("sk-b", Ok);

        using HttpResponseMessage response = await host.PostAsync(Body("gpt-4o"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-p", "sk-b"], host.Upstream.SecretsTried);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord failed = record.Attempts[0];
        Assert.Equal("primary", failed.ChannelName);
        Assert.Equal(FailureClass.UpstreamServerError, failed.FailureClass);
        Assert.Equal(CooldownScope.ChannelModel, failed.CooldownScope);
        Assert.NotNull(failed.CooldownUntil);
    }

    [Fact]
    public async Task AChannelCooledByTheFirstRequestIsSkippedByTheSecond()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("primary", 10, ["sk-p"]), ("backup", 0, ["sk-b"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Offer(channels[1], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        host.Upstream.Script("sk-p", ServerError);
        host.Upstream.Script("sk-b", Ok);

        using HttpResponseMessage first = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage second = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // 第二次不该再去撞正在冷却的 primary：半开探测要等冷却到期。
        Assert.Equal(["sk-p", "sk-b", "sk-b"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task WhenEverythingIsCoolingTheClientGetsRetryAfter()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        host.Upstream.Script("sk-a", ServerError);

        using HttpResponseMessage first = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);

        using HttpResponseMessage second = await host.PostAsync(Body("gpt-4o"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.True(second.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        string payload = await second.Content.ReadAsStringAsync();
        Assert.Contains("channels_cooling", payload, StringComparison.Ordinal);

        // 第二次根本没碰上游。
        Assert.Single(host.Upstream.Calls);
    }

    [Fact]
    public async Task AnAuthFailureCoolsTheKeyAcrossEveryModel()
    {
        // 401 是密钥级：同一把密钥的另一个模型也不该再试（Q28）。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o", "gpt-4o-mini");
                Publish(db, "gpt-4o", "gpt-4o");
                Publish(db, "mini", "gpt-4o-mini");
            });

        host.Upstream.Script("sk-a", Unauthorized);

        using HttpResponseMessage first = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);

        using HttpResponseMessage second = await host.PostAsync(Body("mini"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Single(host.Upstream.Calls);

        var cooldowns = host.Services.GetRequiredService<CooldownRegistry>();
        Assert.Contains(cooldowns.Active(), e => e.Key.Scope == CooldownScope.Key);
    }

    [Fact]
    public async Task AnUpstreamRetryAfterSetsTheCooldownInsteadOfTheLadder()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        host.Upstream.Script("sk-a", () =>
        {
            HttpResponseMessage response = ScriptedUpstream.Json(
                HttpStatusCode.TooManyRequests,
                "{\"error\":{\"message\":\"slow down\"}}");
            response.Headers.TryAddWithoutValidation("retry-after", "600");
            return response;
        });

        using HttpResponseMessage response = await host.PostAsync(Body("gpt-4o"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(FailureClass.RateLimited, attempt.FailureClass);
        Assert.Equal(CooldownScope.KeyModel, attempt.CooldownScope);

        // 上游说 600 秒就是 600 秒，不走 1 分钟起步的指数退避。
        TimeSpan wait = attempt.CooldownUntil!.Value - attempt.FinishedAt!.Value;
        Assert.InRange(wait.TotalSeconds, 590, 610);
    }

    [Fact]
    public async Task TheModelsEndpointListsPublishedModelsInTheOpenAiShape()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o-mini", "gpt-4o");
                Publish(db, "fast", "gpt-4o-mini");
                Publish(db, "smart", "gpt-4o");

                // 停用的不该出现。
                var disabled = new PublishedModel { Name = "draft", Enabled = false };
                disabled.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = "gpt-4o" });
                db.PublishedModels.Add(disabled);

                // 启用了但一个候选都没有的也不该出现。
                Publish(db, "ghost", "model-nobody-has");
            });

        using HttpResponseMessage response = await host.GetModelsAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("list", document.RootElement.GetProperty("object").GetString());
        string[] ids = [.. document.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)];
        Assert.Equal(["fast", "smart"], ids);
        Assert.Equal(
            "model",
            document.RootElement.GetProperty("data")[0].GetProperty("object").GetString());
    }

    [Fact]
    public async Task TheModelsEndpointAnswersAnthropicClientsInTheirOwnShape()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");

                var model = new PublishedModel { Name = "claude-ish", DisplayName = "好用的那个", Enabled = true };
                model.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = "gpt-4o" });
                db.PublishedModels.Add(model);
            });

        using HttpResponseMessage response = await host.GetModelsAsync(anthropic: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement entry = document.RootElement.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("model", entry.GetProperty("type").GetString());
        Assert.Equal("claude-ish", entry.GetProperty("id").GetString());
        Assert.Equal("好用的那个", entry.GetProperty("display_name").GetString());
        Assert.False(document.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal("claude-ish", document.RootElement.GetProperty("first_id").GetString());
    }

    [Fact]
    public async Task AnthropicClientsSendingTheKeyAsBearerStillGetTheirOwnShape()
    {
        // 新版 Anthropic SDK 和 Claude Code 的 ANTHROPIC_AUTH_TOKEN 用 Bearer 带密钥，但总会带 anthropic-version。
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        using var anthropic = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        anthropic.Headers.TryAddWithoutValidation("Authorization", "Bearer " + RelayHost.ApiKeySecret);
        anthropic.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
        using HttpResponseMessage anthropicResponse = await host.Client.SendAsync(anthropic);
        using JsonDocument anthropicBody = JsonDocument.Parse(await anthropicResponse.Content.ReadAsStringAsync());

        using HttpResponseMessage openAiResponse = await host.GetModelsAsync();
        using JsonDocument openAiBody = JsonDocument.Parse(await openAiResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, anthropicResponse.StatusCode);
        Assert.Equal("model", anthropicBody.RootElement.GetProperty("data")[0].GetProperty("type").GetString());
        Assert.True(anthropicBody.RootElement.TryGetProperty("has_more", out _));
        Assert.Equal("list", openAiBody.RootElement.GetProperty("object").GetString());
        Assert.False(openAiBody.RootElement.TryGetProperty("has_more", out _));
    }

    [Fact]
    public async Task TheModelsEndpointNeedsAnApiKey()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Publish(db, "gpt-4o", "gpt-4o");
            });

        using HttpResponseMessage response = await host.GetModelsAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARuleScopedToOneChannelKeepsTheOtherOut()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"]), ("b", 0, ["sk-b"])],
            (db, channels) =>
            {
                Offer(channels[0], "gpt-4o");
                Offer(channels[1], "gpt-4o");

                var model = new PublishedModel { Name = "only-a", Enabled = true };
                model.Rules.Add(new MatchRule
                {
                    Ordinal = 1,
                    Kind = MatchKind.Exact,
                    Pattern = "gpt-4o",
                    ChannelId = channels[0].Id,
                });
                db.PublishedModels.Add(model);
            });

        host.Upstream.Script("sk-a", ServerError);
        host.Upstream.Script("sk-b", Ok);

        using HttpResponseMessage response = await host.PostAsync(Body("only-a"));

        // 规则只认 a，所以 a 挂了就没有别的候选，不会偷偷用 b。
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["sk-a"], host.Upstream.SecretsTried);
    }
}
