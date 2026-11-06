using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 嵌入和图片生成（Q220–Q235，ADR-0018）：只在 OpenAI 格式之间透传，复用选路、冷却、计价和记录。
/// </summary>
public sealed class PassthroughKindRelayTests
{
    private const string EmbeddingBody = "{\"model\":\"embed\",\"input\":[\"hello\",\"world\"]}";
    private const string ImageBody = "{\"model\":\"paint\",\"prompt\":\"a cat\",\"size\":\"1024x1024\"}";

    private const string EmbeddingReply =
        "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]},"
        + "{\"object\":\"embedding\",\"index\":1,\"embedding\":[0.3,0.4]}],"
        + "\"model\":\"text-embedding-3-small\",\"usage\":{\"prompt_tokens\":8,\"total_tokens\":8}}";

    private const string ChatBody = "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string ChatReply =
        "{\"id\":\"c1\",\"object\":\"chat.completion\",\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,"
        + "\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
        + "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}";

    /// <summary>
    /// 种一个嵌入模型 <c>embed</c>（上游叫 text-embedding-3-small）、一个图片模型 <c>paint</c>（上游叫 gpt-image-1）
    /// 和一个对话模型 <c>gpt-4o</c>，每个渠道都提供这三个上游模型。
    /// </summary>
    private static Action<ConfigDbContext, IReadOnlyList<Channel>> Seed(
        Action<ConfigDbContext, IReadOnlyList<Channel>>? extra = null) =>
        (db, channels) =>
        {
            foreach (Channel channel in channels)
            {
                channel.Provider = "openai";
                foreach (string name in new[] { "text-embedding-3-small", "gpt-image-1", "gpt-4o" })
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = name, Source = UpstreamModelSource.Manual });
                }
            }

            Publish(db, "embed", RequestKind.Embedding, "text-embedding-3-small");
            Publish(db, "paint", RequestKind.Image, "gpt-image-1");
            Publish(db, "gpt-4o", RequestKind.Chat, "gpt-4o");
            extra?.Invoke(db, channels);
        };

    private static void Publish(ConfigDbContext db, string name, RequestKind kind, string upstream)
    {
        var model = new PublishedModel { Name = name, RequestKind = kind, Enabled = true };
        model.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = upstream });
        db.PublishedModels.Add(model);
    }

    private static string ImageReply(int count) =>
        "{\"created\":1,\"data\":["
        + string.Join(",", Enumerable.Range(0, count).Select(_ => "{\"b64_json\":\"aGVsbG8=\"}"))
        + "],\"usage\":{\"input_tokens\":10,\"output_tokens\":4160,\"total_tokens\":4170}}";

    [Fact]
    public async Task EmbeddingsPassThroughWithTheUpstreamModelNameAndRecordUsage()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, EmbeddingReply));

        using HttpResponseMessage response = await host.PostToAsync("/v1/embeddings", EmbeddingBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EmbeddingReply, await response.Content.ReadAsStringAsync());

        (string _, string url, string body) = Assert.Single(host.Upstream.Calls);
        Assert.Equal("https://a.example/v1/embeddings", url);
        using (JsonDocument sent = JsonDocument.Parse(body))
        {
            Assert.Equal("text-embedding-3-small", sent.RootElement.GetProperty("model").GetString());
            Assert.Equal(2, sent.RootElement.GetProperty("input").GetArrayLength());
            Assert.False(sent.RootElement.TryGetProperty("stream_options", out _));
        }

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestKind.Embedding, record.RequestKind);
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(8, record.InputTokens);
        Assert.Equal(0, record.OutputTokens);
        Assert.Equal(UsageSource.Upstream, record.UsageSource);
        Assert.Null(record.Affinity);
    }

    [Fact]
    public async Task EmbeddingsWithoutUsageAreEstimatedFromTheInput()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(
            HttpStatusCode.OK,
            "{\"object\":\"list\",\"data\":[{\"embedding\":[0.1]}]}"));

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/embeddings",
            "{\"model\":\"embed\",\"input\":[[1,2,3,4],\"abcdefgh\"]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());

        // 四个 Token id + 8 个 ASCII 字符（两个 Token）。
        Assert.Equal(6, record.InputTokens);
        Assert.Equal(UsageSource.GatewayEstimated, record.UsageSource);
    }

    [Fact]
    public async Task ACustomChatPathIsSwappedForTheKindEndpoint()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 1, ["sk-a"]), ("b", 0, ["sk-b"])],
            Seed((_, channels) =>
            {
                // 高优先级的 a 路径推不出嵌入端点，不该成为候选；b 的路径以 /chat/completions 结尾，换后缀。
                channels[0].Path = "/custom/generate";
                channels[1].Path = "/api/v3/chat/completions?ver=2";
            }));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, EmbeddingReply));

        using HttpResponseMessage response = await host.PostToAsync("/v1/embeddings", EmbeddingBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        (string secret, string url, string _) = Assert.Single(host.Upstream.Calls);
        Assert.Equal("sk-b", secret);
        Assert.Equal("https://b.example/api/v3/embeddings?ver=2", url);
    }

    [Fact]
    public async Task AModelOfAnotherKindPointsAtTheRightEndpoint()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());

        using HttpResponseMessage response = await host.PostToAsync("/v1/embeddings", ImageBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.Contains("model_not_found", text);
        Assert.Contains("/v1/images/generations", text);
        Assert.Empty(host.Upstream.Calls);

        using HttpResponseMessage chat = await host.PostAsync(EmbeddingBody.Replace("\"input\"", "\"messages\""));
        Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
        Assert.Contains("/v1/embeddings", await chat.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARequestWithoutAModelIsRejected()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());

        using HttpResponseMessage response = await host.PostToAsync("/v1/images/generations", "{\"prompt\":\"a cat\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("model", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task ImagesArePricedPerImageWhenThePriceIsSet()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Seed((db, channels) =>
            {
                channels[0].MultiplierBasisPoints = 15000;
                db.PriceRows.Add(new PriceRow
                {
                    Provider = "openai",
                    UpstreamModel = "gpt-image-1",
                    Source = PriceSource.Manual,
                    InputPrice = 5m,
                    OutputPrice = 40m,
                    ImagePrice = 0.04m,
                });
            }));
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, ImageReply(2)));

        using HttpResponseMessage response = await host.PostToAsync("/v1/images/generations", ImageBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        AttemptRecord attempt = Assert.Single(record.Attempts);
        Assert.Equal(RequestKind.Image, record.RequestKind);
        Assert.Equal(2, record.ImageCount);
        Assert.Equal(2, attempt.ImageCount);
        Assert.Equal(0.04m, attempt.ImagePrice);
        Assert.Null(attempt.InputPrice);

        // 2 张 × 0.04 美元 × 1.5 倍 = 0.12 美元，不看 Token。
        Assert.Equal(120_000_000, record.CostNanoUsd);
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(4160, record.OutputTokens);
    }

    [Fact]
    public async Task ImagesFallBackToTokenPricesAndOtherwiseStayUnpriced()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Seed((db, _) => db.PriceRows.Add(new PriceRow
            {
                Provider = "openai",
                UpstreamModel = "gpt-image-1",
                Source = PriceSource.Manual,
                InputPrice = 5m,
                OutputPrice = 40m,
            })));

        // 第一次 OpenAI 形状、带用量：按 Token 算。第二次硅基流动形状（images[]）、没有用量：算不出来。
        var replies = new Queue<string>(
        [
            ImageReply(1),
            "{\"images\":[{\"url\":\"https://img.example/1.png\"}],\"timings\":{\"inference\":1.2},\"seed\":7}",
        ]);
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, replies.Dequeue()));

        using (HttpResponseMessage first = await host.PostToAsync("/v1/images/generations", ImageBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (HttpResponseMessage second = await host.PostToAsync("/v1/images/generations", ImageBody))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("img.example", await second.Content.ReadAsStringAsync());
        }

        List<RequestRecord> records = await host.ReadRecordsAsync();
        Assert.Equal(2, records.Count);

        // 10 × 5 + 4160 × 40 美元/百万 Token = 0.16645 美元。
        Assert.Equal(166_450_000, records[0].CostNanoUsd);
        Assert.Equal(1, records[0].ImageCount);

        Assert.Equal(1, records[1].ImageCount);
        Assert.Null(records[1].CostNanoUsd);
        Assert.Equal(0, records[1].InputTokens);
        Assert.Equal(UsageSource.Unavailable, records[1].UsageSource);
    }

    [Fact]
    public async Task AnEmptyImageListTriesTheNextCandidate()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 1, ["sk-a"]), ("b", 0, ["sk-b"])],
            Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, "{\"created\":1,\"data\":[]}"));
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, ImageReply(1)));

        using HttpResponseMessage response = await host.PostToAsync("/v1/images/generations", ImageBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-a", "sk-b"], host.Upstream.SecretsTried);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(FailureClass.SoftError, record.Attempts[0].FailureClass);
        Assert.Equal(1, record.ImageCount);
    }

    [Fact]
    public async Task ImageStreamsPassThroughAndCountCompletedImages()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-a"])],
            Seed((db, _) => db.PriceRows.Add(new PriceRow
            {
                Provider = "openai",
                UpstreamModel = "gpt-image-1",
                Source = PriceSource.Manual,
                ImagePrice = 0.1m,
            })));
        string[] frames =
        [
            "event: image_generation.partial_image\ndata: {\"type\":\"image_generation.partial_image\",\"b64_json\":\"cGFydA==\",\"partial_image_index\":0}\n\n",
            "event: image_generation.completed\ndata: {\"type\":\"image_generation.completed\",\"b64_json\":\"ZnVsbA==\",\"usage\":{\"input_tokens\":10,\"output_tokens\":100,\"total_tokens\":110}}\n\n",
        ];
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(frames));

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/images/generations",
            "{\"model\":\"paint\",\"prompt\":\"a cat\",\"stream\":true,\"partial_images\":1}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Concat(frames), await response.Content.ReadAsStringAsync());

        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.True(record.IsStream);
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(1, record.ImageCount);
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(100_000_000, record.CostNanoUsd);
    }

    [Fact]
    public async Task VolcengineImageStreamsCountSucceededImages()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Sse(
            "data: {\"type\":\"image_generation.partial_succeeded\",\"url\":\"https://img.example/1.png\",\"image_index\":0}\n\n",
            "data: {\"type\":\"image_generation.partial_failed\",\"error\":{\"code\":\"OutputImageSensitiveContentDetected\",\"message\":\"blocked\"},\"image_index\":1}\n\n",
            "data: {\"type\":\"image_generation.completed\",\"usage\":{\"generated_images\":1,\"output_tokens\":4096,\"total_tokens\":4096}}\n\n",
            "data: [DONE]\n\n"));

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/images/generations",
            "{\"model\":\"paint\",\"prompt\":\"a cat\",\"stream\":true}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestStatus.Succeeded, record.Status);
        Assert.Equal(1, record.ImageCount);
        Assert.Equal(4096, record.OutputTokens);
    }

    [Fact]
    public async Task AJsonReplyToAStreamRequestIsPassedThrough()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, ImageReply(1)));

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/images/generations",
            "{\"model\":\"paint\",\"prompt\":\"a cat\",\"stream\":true}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ImageReply(1), await response.Content.ReadAsStringAsync());
        Assert.Equal(1, Assert.Single(await host.ReadRecordsAsync()).ImageCount);
    }

    [Fact]
    public async Task AMissingEndpointCoolsOnlyThatModelOnTheChannel()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 1, ["sk-a"]), ("b", 0, ["sk-b"])],
            Seed());
        var replies = new Queue<Func<HttpResponseMessage>>(
        [
            () => ScriptedUpstream.Json(HttpStatusCode.NotFound, "404 page"),
            () => ScriptedUpstream.Json(HttpStatusCode.OK, ChatReply),
        ]);
        host.Upstream.Script("sk-a", () => replies.Dequeue()());
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, EmbeddingReply));

        using (HttpResponseMessage embedding = await host.PostToAsync("/v1/embeddings", EmbeddingBody))
        {
            Assert.Equal(HttpStatusCode.OK, embedding.StatusCode);
        }

        // 渠道 a 没有 /v1/embeddings：只冷却 (a, text-embedding-3-small)，对话照样落到 a。
        (CooldownKey key, DateTimeOffset _, FailureClass failure) =
            Assert.Single(host.Services.GetRequiredService<CooldownRegistry>().Active());
        Assert.Equal(CooldownScope.ChannelModel, key.Scope);
        Assert.Equal("text-embedding-3-small", key.UpstreamModel);
        Assert.Equal(FailureClass.ModelNotFound, failure);

        using (HttpResponseMessage chat = await host.PostAsync(ChatBody))
        {
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        }

        Assert.Equal(["sk-a", "sk-b", "sk-a"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task AnImageTimeoutDoesNotTryAnotherCandidate()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 1, ["sk-a"]), ("b", 0, ["sk-b"])],
            Seed(),
            new ForwardTimeouts { ImageFirstEvent = TimeSpan.FromMilliseconds(300), FirstEvent = TimeSpan.FromMilliseconds(300) });
        host.Upstream.ScriptHanging("sk-a");
        host.Upstream.Script("sk-b", () => ScriptedUpstream.Json(HttpStatusCode.OK, ImageReply(1)));

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/images/generations",
            "{\"model\":\"paint\",\"prompt\":\"a cat\",\"stream\":true}");

        // 超时的那次上游可能已经在画、已经扣费：不换候选再画一次（Q234）。
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["sk-a"], host.Upstream.SecretsTried);
    }

    [Fact]
    public async Task TheAnthropicModelListOnlyShowsChatModels()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());

        using HttpResponseMessage openAi = await host.GetModelsAsync();
        using HttpResponseMessage anthropic = await host.GetModelsAsync(anthropic: true);

        string all = await openAi.Content.ReadAsStringAsync();
        Assert.Contains("\"embed\"", all);
        Assert.Contains("\"paint\"", all);
        Assert.Contains("\"gpt-4o\"", all);

        string chatOnly = await anthropic.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"embed\"", chatOnly);
        Assert.DoesNotContain("\"paint\"", chatOnly);
        Assert.Contains("\"gpt-4o\"", chatOnly);
    }

    [Fact]
    public async Task NonOpenAiChannelsAreNotCandidates()
    {
        await using RelayHost host = await RelayHost.StartWithFormatsAsync(
            ("claude", 0, ["sk-c"], ApiFormat.Anthropic));

        // 默认种子把下发模型都种成对话类；把 gpt-4o 改成嵌入类再重新加载快照。
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            PublishedModel model = db.PublishedModels.Single(m => m.Name == "gpt-4o");
            model.RequestKind = RequestKind.Embedding;
            await db.SaveChangesAsync();
        }

        await host.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();

        using HttpResponseMessage response = await host.PostToAsync(
            "/v1/embeddings",
            "{\"model\":\"gpt-4o\",\"input\":\"hi\"}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(host.Upstream.Calls);
    }

    [Fact]
    public async Task TheWorkbenchCanTestAChannelWithAnEmbeddingRequest()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync([("a", 0, ["sk-a"])], Seed());
        host.Upstream.Script("sk-a", () => ScriptedUpstream.Json(HttpStatusCode.OK, EmbeddingReply));
        int channelId = host.Services.GetRequiredService<ConfigSnapshotProvider>().Current.Channels.Single().ChannelId;

        using HttpResponseMessage response = await host.PostWorkbenchAsync(
            $"/workbench/channels/{channelId}/send?kind=embedding",
            "{\"model\":\"text-embedding-3-small\",\"input\":\"hi\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://a.example/v1/embeddings", Assert.Single(host.Upstream.Calls).Url);
        RequestRecord record = Assert.Single(await host.ReadRecordsAsync());
        Assert.Equal(RequestOrigin.Workbench, record.Origin);
        Assert.Equal(RequestKind.Embedding, record.RequestKind);
    }
}
