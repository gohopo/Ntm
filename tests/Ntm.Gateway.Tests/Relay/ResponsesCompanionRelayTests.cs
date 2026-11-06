using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// Responses 配套（Q268–Q287，ADR-0020）：按 id 的资源回到产出它的渠道密钥，落点存进库，
/// background 的用量取回时回填，UpstreamConversation 和 input_tokens、compact。
/// </summary>
public sealed class ResponsesCompanionRelayTests
{
    private const string Created = """{"id":"resp_1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}],"usage":{"input_tokens":3,"output_tokens":1}}""";

    private static Task<RelayHost> StartTwoKeysAsync() =>
        RelayHost.StartWithFormatsAsync(("resp", 0, ["sk-a", "sk-b"], ApiFormat.OpenAiResponses));

    private static async Task UpdateConfigAsync(RelayHost host, Func<ConfigDbContext, Task> update)
    {
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await update(scope.ServiceProvider.GetRequiredService<ConfigDbContext>());
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
    }

    /// <summary>第一把密钥 sk-a 回 401，于是 resp_1 落在 sk-b 上。</summary>
    private static async Task<RelayHost> StartWithLandingOnSecondKeyAsync(Func<HttpMethod, Uri, string, HttpResponseMessage>? byId = null)
    {
        RelayHost host = await StartTwoKeysAsync();
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}"""));
        host.Upstream.Script("sk-b", (method, uri, body) =>
            uri.AbsolutePath == "/v1/responses" && method == HttpMethod.Post
                ? ScriptedUpstream.Json(HttpStatusCode.OK, Created)
                : byId?.Invoke(method, uri, body) ?? ScriptedUpstream.Json(HttpStatusCode.OK, """{"id":"resp_1","object":"response","status":"completed"}"""));

        using HttpResponseMessage created = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi"}""");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        host.Upstream.Calls.Clear();
        host.Upstream.Methods.Clear();
        return host;
    }

    [Fact]
    public async Task ByIdEndpointsGoBackToTheProducingKeyWithMethodAndQuery()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync((method, uri, _) =>
            method == HttpMethod.Delete
                ? ScriptedUpstream.Json(HttpStatusCode.OK, """{"id":"resp_1","object":"response.deleted","deleted":true}""")
                : ScriptedUpstream.Json(HttpStatusCode.OK, """{"object":"list","data":[]}"""));

        using (HttpResponseMessage got = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1?include[]=reasoning.encrypted_content"))
        {
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        }

        using (HttpResponseMessage items = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1/input_items?limit=5&order=asc"))
        {
            Assert.Equal(HttpStatusCode.OK, items.StatusCode);
            Assert.Contains("\"list\"", await items.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage cancel = await host.SendAsync(HttpMethod.Post, "/v1/responses/resp_1/cancel", "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        using (HttpResponseMessage deleted = await host.SendAsync(HttpMethod.Delete, "/v1/responses/resp_1"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Contains("response.deleted", await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(["sk-b", "sk-b", "sk-b", "sk-b"], host.Upstream.SecretsTried);
        Assert.Equal(
            [
                "https://resp.example/v1/responses/resp_1?include[]=reasoning.encrypted_content",
                "https://resp.example/v1/responses/resp_1/input_items?limit=5&order=asc",
                "https://resp.example/v1/responses/resp_1/cancel",
                "https://resp.example/v1/responses/resp_1",
            ],
            host.Upstream.Calls.Select(c => Uri.UnescapeDataString(c.Url)));
        Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete], host.Upstream.Methods);

        // 上游删掉了，落点跟着删：再取回就是网关不认识的 id，不转发。
        host.Upstream.Calls.Clear();
        using HttpResponseMessage gone = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task UnknownIdsAndOtherApiKeysGet404WithoutForwarding()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync();
        const string other = "ntm-other-token-0123456789";
        await UpdateConfigAsync(host, db =>
        {
            db.ApiKeys.Add(new ApiKey
            {
                Name = "other",
                Fingerprint = SecretFingerprint.Compute(other),
                Prefix = SecretFingerprint.Prefix(other),
            });
            return db.SaveChangesAsync();
        });

        using HttpResponseMessage unknown = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_nope");
        using HttpResponseMessage foreign = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1", token: other);
        using HttpResponseMessage anonymous = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1", token: null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("not_found", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task ADeadProducingKeyIs502AndAnUpstream401IsNotPassedOn()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync((_, _, _) =>
            ScriptedUpstream.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"revoked"}}"""));

        using (HttpResponseMessage rejected = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
        }

        string fingerprint = SecretFingerprint.Compute("sk-b");
        await UpdateConfigAsync(host, db => db.ChannelKeys
            .Where(k => k.SecretFingerprint == fingerprint)
            .ExecuteUpdateAsync(set => set.SetProperty(k => k.Enabled, false)));
        host.Upstream.Calls.Clear();

        using HttpResponseMessage dead = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1");

        Assert.Equal(HttpStatusCode.BadGateway, dead.StatusCode);
        Assert.Contains("渠道密钥现在用不了", await dead.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task KeysSharingAnAccountLetLaterTurnsAndByIdCallsUseAnotherKey()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync();
        host.Upstream.Script("sk-a", (method, _, _) => method == HttpMethod.Post
            ? ScriptedUpstream.Json(HttpStatusCode.OK, Created.Replace("resp_1", "resp_2", StringComparison.Ordinal))
            : ScriptedUpstream.Json(HttpStatusCode.OK, """{"id":"resp_1","object":"response","status":"completed"}"""));
        ChannelSnapshot channel = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single();
        host.Services.GetRequiredService<CooldownRegistry>()
            .Clear(channel.ChannelId, "gpt-4o", SecretFingerprint.Compute("sk-a"));
        const string nextTurn = """{"model":"gpt-4o","input":"again","previous_response_id":"resp_1"}""";

        // 开了开关，产出它的密钥还能用时仍然先用它。
        await UpdateConfigAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.KeysShareAccount, true)));
        using (HttpResponseMessage same = await host.PostResponsesAsync(nextTurn))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        }

        Assert.Equal(["sk-b"], host.Upstream.SecretsTried);

        string fingerprint = SecretFingerprint.Compute("sk-b");
        await UpdateConfigAsync(host, db => db.ChannelKeys
            .Where(k => k.SecretFingerprint == fingerprint)
            .ExecuteUpdateAsync(set => set.SetProperty(k => k.Enabled, false)));

        // 关着开关：落点那把密钥停用了，下一轮只能 400。
        await UpdateConfigAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.KeysShareAccount, false)));
        using (HttpResponseMessage stale = await host.PostResponsesAsync(nextTurn))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        }

        // 开着开关：同渠道的另一把密钥接着做，按 id 的接口也改用它。
        await UpdateConfigAsync(host, db => db.Channels.ExecuteUpdateAsync(set => set.SetProperty(c => c.KeysShareAccount, true)));
        host.Upstream.Calls.Clear();
        using (HttpResponseMessage moved = await host.PostResponsesAsync(nextTurn))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        using (HttpResponseMessage byId = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1"))
        {
            Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        }

        Assert.All(host.Upstream.Calls, call => Assert.Equal("sk-a", call.Secret));
        Assert.Equal(2, host.Upstream.Calls.Count);
    }

    [Fact]
    public async Task LandingsArePersistedAndFoundAfterARestart()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync();
        int apiKeyId = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current
            .FindApiKey(SecretFingerprint.Compute(RelayHost.ApiKeySecret))!.ApiKeyId;

        ResponseLanding? stored = null;
        for (int i = 0; i < 50 && stored is null; i++)
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            stored = await scope.ServiceProvider.GetRequiredService<TrafficDbContext>()
                .ResponseLandings.AsNoTracking().SingleOrDefaultAsync(l => l.Id == "resp_1");
            if (stored is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(stored);
        Assert.Equal(SecretFingerprint.Compute("sk-b"), stored.KeyFingerprint);
        Assert.Equal("gpt-4o", stored.PublishedModel);

        // 重启后内存里的表是空的：新表只能从库里找到它。
        var fresh = new ResponseLandingTable(TimeProvider.System, host.Services.GetRequiredService<IServiceScopeFactory>());
        ResponseLanding? found = await fresh.FindAsync("resp_1", ResponseLandingKind.Response, apiKeyId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(stored.ChannelId, found.ChannelId);
        Assert.Null(await fresh.FindAsync("resp_1", ResponseLandingKind.Response, apiKeyId + 1, CancellationToken.None));
        Assert.Null(await fresh.FindAsync("resp_1", ResponseLandingKind.Conversation, apiKeyId, CancellationToken.None));
    }

    [Fact]
    public async Task BackgroundGoesToResponsesChannelsAndUsageIsBackfilledOnce()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("chat", 10, ["sk-c"], ApiFormat.OpenAiChat),
            ("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        const string done = """{"id":"resp_bg","object":"response","status":"completed","output":[],"usage":{"input_tokens":40,"output_tokens":7}}""";
        host.Upstream.Script("sk-r", (method, _, _) => method == HttpMethod.Post
            ? ScriptedUpstream.Json(HttpStatusCode.OK, """{"id":"resp_bg","object":"response","status":"queued","background":true,"output":[]}""")
            : ScriptedUpstream.Json(HttpStatusCode.OK, done));

        using HttpResponseMessage queued = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi","background":true}""");
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Contains("queued", await queued.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Guid requestId = Guid.Parse(queued.Headers.GetValues("x-ntm-request-id").Single());

        // 原请求的记录得先写进库，回填才找得到它。
        for (int i = 0; i < 50; i++)
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<TrafficDbContext>().Requests.AnyAsync(r => r.Id == requestId))
            {
                break;
            }

            await Task.Delay(100);
        }

        using (HttpResponseMessage first = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_bg"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_bg"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        List<RequestRecord> records = await host.ReadRecordsAsync();
        RequestRecord record = Assert.Single(records);
        Assert.Equal(40, record.InputTokens);
        Assert.Equal(7, record.OutputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal("resp_bg", attempt.ResponseId);
        Assert.Equal(40, attempt.InputTokens);
        Assert.Equal(40, (await host.ReadAggregatesAsync()).Sum(a => a.InputTokens));
        Assert.DoesNotContain("sk-c", host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task StreamedRetrievalIsPassedThroughWithItsQuery()
    {
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync((_, _, _) => ScriptedUpstream.Sse(
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":4,\"delta\":\"x\"}\n\n"));

        using HttpResponseMessage streamed = await host.SendAsync(HttpMethod.Get, "/v1/responses/resp_1?stream=true&starting_after=3");

        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sequence_number\":4", await streamed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("https://resp.example/v1/responses/resp_1?stream=true&starting_after=3", Assert.Single(host.Upstream.Calls).Url);
    }

    [Fact]
    public async Task ConversationsAreCreatedOnTheFirstUsableChannelAndPinLaterTurns()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("low", 1, ["sk-low"], ApiFormat.OpenAiResponses),
            ("high", 5, ["sk-high"], ApiFormat.OpenAiResponses),
            ("chat", 9, ["sk-chat"], ApiFormat.OpenAiChat));
        foreach (string secret in new[] { "sk-low", "sk-high" })
        {
            host.Upstream.Script(secret, (method, uri, _) => uri.AbsolutePath switch
            {
                "/v1/conversations" => ScriptedUpstream.Json(HttpStatusCode.OK, """{"id":"conv_1","object":"conversation"}"""),
                "/v1/responses" => ScriptedUpstream.Json(HttpStatusCode.OK, Created),
                _ => ScriptedUpstream.Json(HttpStatusCode.OK, """{"object":"list","data":[]}"""),
            });
        }

        using (HttpResponseMessage created = await host.SendAsync(HttpMethod.Post, "/v1/conversations", """{"items":[]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        Assert.Equal("sk-high", Assert.Single(host.Upstream.Calls).Secret);

        // 优先级改了也不搬家：对话在 high 的账号里。
        await UpdateConfigAsync(host, db => db.Channels
            .Where(c => c.Name == "low")
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Priority, 50)));
        host.Upstream.Calls.Clear();

        using (HttpResponseMessage turn = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi","conversation":"conv_1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        }

        using (HttpResponseMessage items = await host.SendAsync(HttpMethod.Get, "/v1/conversations/conv_1/items?limit=2"))
        {
            Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        }

        using (HttpResponseMessage item = await host.SendAsync(HttpMethod.Delete, "/v1/conversations/conv_1/items/msg_9"))
        {
            Assert.Equal(HttpStatusCode.OK, item.StatusCode);
        }

        Assert.Equal(["sk-high", "sk-high", "sk-high"], host.Upstream.SecretsTried);
        Assert.Equal(
            [
                "https://high.example/v1/responses",
                "https://high.example/v1/conversations/conv_1/items?limit=2",
                "https://high.example/v1/conversations/conv_1/items/msg_9",
            ],
            host.Upstream.Calls.Select(c => c.Url));

        using HttpResponseMessage stale = await host.PostResponsesAsync("""{"model":"gpt-4o","input":"hi","conversation":"conv_nope"}""");
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("重新建一个对话", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InputTokensFollowsTheLandingOrRoutesAndWritesNoRecord()
    {
        // sk-a 建 resp_1 时回了 401，正在冷却：选路也只会挑 sk-b，落点同样是 sk-b。
        await using RelayHost host = await StartWithLandingOnSecondKeyAsync((_, uri, _) =>
            uri.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal)
                ? ScriptedUpstream.Json(HttpStatusCode.OK, """{"object":"response.input_tokens","input_tokens":11}""")
                : ScriptedUpstream.Json(HttpStatusCode.NotFound, "{}"));

        using (HttpResponseMessage routed = await host.SendAsync(HttpMethod.Post, "/v1/responses/input_tokens", """{"model":"gpt-4o","input":"hi"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
            Assert.Contains("\"input_tokens\":11", await routed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage anchored = await host.SendAsync(
            HttpMethod.Post,
            "/v1/responses/input_tokens",
            """{"model":"gpt-4o","previous_response_id":"resp_1","input":"more"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, anchored.StatusCode);
        }

        using (HttpResponseMessage both = await host.SendAsync(
            HttpMethod.Post,
            "/v1/responses/input_tokens",
            """{"model":"gpt-4o","previous_response_id":"resp_1","conversation":"conv_1","input":"x"}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        }

        Assert.Equal(["sk-b", "sk-b"], host.Upstream.SecretsTried);
        Assert.All(host.Upstream.Calls, c => Assert.Equal("https://resp.example/v1/responses/input_tokens", c.Url));

        // 只有建 resp_1 那一次请求留了记录。
        Assert.Single(await host.ReadRecordsAsync());
    }

    [Fact]
    public async Task CompactIsRoutedAndBilledButLeavesNoLanding()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("chat", 10, ["sk-c"], ApiFormat.OpenAiChat),
            ("resp", 0, ["sk-r"], ApiFormat.OpenAiResponses));
        host.Upstream.Script("sk-r", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            """{"id":"resp_c","object":"response.compaction","output":[{"type":"compaction","id":"cmp_1","encrypted_content":"x"}],"usage":{"input_tokens":9,"output_tokens":2}}"""));

        using HttpResponseMessage compacted = await host.PostToAsync("/v1/responses/compact", """{"model":"gpt-4o","input":"long history"}""");

        Assert.Equal(HttpStatusCode.OK, compacted.StatusCode);
        Assert.Equal("https://resp.example/v1/responses/compact", Assert.Single(host.Upstream.Calls).Url);
        Assert.Equal(0, host.Services.GetRequiredService<ResponseLandingTable>().Count);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(9, record.InputTokens);
    }

    [Theory]
    [InlineData(null, "", "https://x.example/v1/responses/resp_1/cancel", "https://x.example/v1/conversations/conv_1/items")]
    [InlineData("/api/v3/responses?ak=1", "?q=2", "https://x.example/api/v3/responses/resp_1/cancel?ak=1&q=2", "https://x.example/api/v3/conversations/conv_1/items?ak=1&q=2")]
    [InlineData("/openai/chat", "", null, null)]
    public void ResourceAddressesAreDerivedFromTheChannelResponsesPath(string? path, string query, string? response, string? conversation)
    {
        var channel = new ChannelSnapshot
        {
            ChannelId = 1,
            ChannelName = "x",
            BaseUrl = "https://x.example",
            Path = path,
            ApiFormat = ApiFormat.OpenAiResponses,
            Keys = [],
        };
        QueryString client = query.Length == 0 ? QueryString.Empty : new QueryString(query);

        Assert.Equal(response, ResponsesResourceRelay.ResourceUri(channel, ResponseLandingKind.Response, "resp_1", "/cancel", client)?.ToString());
        Assert.Equal(conversation, ResponsesResourceRelay.ResourceUri(channel, ResponseLandingKind.Conversation, "conv_1", "/items", client)?.ToString());
    }
}
