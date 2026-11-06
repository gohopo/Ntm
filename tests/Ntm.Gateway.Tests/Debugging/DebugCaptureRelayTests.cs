using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Debugging;
using Ntm.Gateway.Tests.Relay;

namespace Ntm.Gateway.Tests.Debugging;

/// <summary>
/// DebugCapture 接在真实转发上（Q155/Q156/Q158/Q161）：三处开关任一开着就录，每次 Attempt 三份原文，
/// 认证头脱敏、正文原样，流式按原始 SSE 字节存。
/// </summary>
public sealed class DebugCaptureRelayTests
{
    private const string Request = "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
    private const string Content = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
    private const string Done = "data: [DONE]\n\n";

    private static HttpResponseMessage OkJson() => ScriptedUpstream.Json(
        HttpStatusCode.OK,
        "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}");

    private static DateTimeOffset InAnHour => DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>自定义 configure 时 RelayHost 不再种默认模型，这里补上再叠加各自的开关。</summary>
    private static Action<ConfigDbContext, IReadOnlyList<Channel>> WithModels(
        Action<ConfigDbContext, IReadOnlyList<Channel>> extra) =>
        (db, channels) =>
        {
            foreach (Channel channel in channels)
            {
                foreach (string model in RelayHost.Models)
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                }
            }

            foreach (string model in RelayHost.Models)
            {
                db.PublishedModels.Add(new PublishedModel
                {
                    Name = model,
                    Enabled = true,
                    Rules = { new MatchRule { Kind = MatchKind.Exact, Pattern = model, Ordinal = 1 } },
                });
            }

            extra(db, channels);
        };

    private static async Task<List<DebugPayload>> WaitForPayloadsAsync(RelayHost host, int count)
    {
        for (int i = 0; i < 100; i++)
        {
            List<DebugPayload> payloads = await ReadPayloadsAsync(host);
            if (payloads.Count >= count)
            {
                return payloads;
            }

            await Task.Delay(50);
        }

        return await ReadPayloadsAsync(host);
    }

    private static async Task<List<DebugPayload>> ReadPayloadsAsync(RelayHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        DebugDbContext db = scope.ServiceProvider.GetRequiredService<DebugDbContext>();
        return await db.DebugPayloads.AsNoTracking()
            .OrderBy(p => p.AttemptOrdinal)
            .ThenBy(p => p.Kind)
            .ToListAsync();
    }

    [Fact]
    public async Task NothingIsCapturedWhileAllSwitchesAreOff()
    {
        await using RelayHost host = await RelayHost.StartAsync(("a", 0, ["sk-upstream-a"]));
        host.Upstream.Script("sk-upstream-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Task.Delay(300);
        Assert.Empty(await ReadPayloadsAsync(host));
        Assert.Equal(0, host.Services.GetRequiredService<DebugCaptureSink>().Written);
    }

    [Fact]
    public async Task TheChannelSwitchCapturesThreePayloadsWithRedactedHeaders()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, channels) =>
            {
                foreach (Channel channel in channels)
                {
                    channel.DebugCaptureUntil = InAnHour;
                }
            }));
        host.Upstream.Script("sk-upstream-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<DebugPayload> payloads = await WaitForPayloadsAsync(host, 3);
        Assert.Equal(
            [DebugPayloadKind.ClientRequest, DebugPayloadKind.UpstreamRequest, DebugPayloadKind.UpstreamResponse],
            payloads.Select(p => p.Kind));
        Assert.All(payloads, p => Assert.Equal(1, p.AttemptOrdinal));

        DebugPayload client = payloads[0];
        Assert.Equal(Request, client.Body);
        Assert.Contains("Authorization: Bearer ntm-te***", client.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain(RelayHost.ApiKeySecret, client.Headers, StringComparison.Ordinal);

        DebugPayload upstream = payloads[1];
        Assert.Contains("Authorization: Bearer sk-ups***", upstream.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-upstream-a", upstream.Headers, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"gpt-4o\"", upstream.Body, StringComparison.Ordinal);

        DebugPayload answer = payloads[2];
        Assert.Equal(200, answer.HttpStatusCode);
        Assert.Contains("\"content\":\"ok\"", answer.Body, StringComparison.Ordinal);
        Assert.False(answer.Truncated);
    }

    [Fact]
    public async Task AStreamIsStoredAsTheRawEventBytes()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, channels) =>
            {
                foreach (Channel channel in channels)
                {
                    channel.DebugCaptureUntil = InAnHour;
                }
            }));
        host.Upstream.Script("sk-upstream-a", () => ScriptedUpstream.Sse(Content, Done));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"gpt-4o\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("你好", body, StringComparison.Ordinal);

        List<DebugPayload> payloads = await WaitForPayloadsAsync(host, 3);
        DebugPayload answer = Assert.Single(payloads, p => p.Kind == DebugPayloadKind.UpstreamResponse);
        Assert.Equal(Content + Done, answer.Body);
        Assert.Contains("text/event-stream", answer.Headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryAttemptOfAFailoverGetsItsOwnPayloads()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 10, ["sk-upstream-a"]), ("b", 0, ["sk-upstream-b"])],
            WithModels((db, channels) =>
            {
                foreach (Channel channel in channels)
                {
                    channel.DebugCaptureUntil = InAnHour;
                }
            }));
        host.Upstream.Script("sk-upstream-a", () => ScriptedUpstream.Json(
            HttpStatusCode.InternalServerError,
            "{\"error\":{\"message\":\"boom\"}}"));
        host.Upstream.Script("sk-upstream-b", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<DebugPayload> payloads = await WaitForPayloadsAsync(host, 6);
        Assert.Equal(6, payloads.Count);
        Assert.Equal([1, 1, 1, 2, 2, 2], payloads.Select(p => p.AttemptOrdinal));

        DebugPayload failed = Assert.Single(payloads, p => p.AttemptOrdinal == 1 && p.Kind == DebugPayloadKind.UpstreamResponse);
        Assert.Equal(500, failed.HttpStatusCode);
        Assert.Contains("boom", failed.Body, StringComparison.Ordinal);

        DebugPayload second = Assert.Single(payloads, p => p.AttemptOrdinal == 2 && p.Kind == DebugPayloadKind.ClientRequest);
        Assert.Equal(Request, second.Body);
    }

    [Fact]
    public async Task TheApiKeySwitchAloneIsEnough()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, _) => db.ApiKeys.Local.Single().DebugCaptureUntil = InAnHour));
        host.Upstream.Script("sk-upstream-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(3, (await WaitForPayloadsAsync(host, 3)).Count);
    }

    [Fact]
    public async Task TheGlobalSwitchAloneIsEnough()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, _) => db.Settings.Add(new GatewaySettings { DebugCaptureUntil = InAnHour })));
        host.Upstream.Script("sk-upstream-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(3, (await WaitForPayloadsAsync(host, 3)).Count);
    }

    [Fact]
    public async Task AnExpiredSwitchCapturesNothing()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, channels) =>
            {
                db.Settings.Add(new GatewaySettings { DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(-1) });
                foreach (Channel channel in channels)
                {
                    channel.DebugCaptureUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
            }));
        host.Upstream.Script("sk-upstream-a", OkJson);

        using HttpResponseMessage response = await host.PostAsync(Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Task.Delay(300);
        Assert.Empty(await ReadPayloadsAsync(host));
    }

    [Fact]
    public async Task ARequestThatNeverReachedAnUpstreamIsNotCaptured()
    {
        await using RelayHost host = await RelayHost.StartWithModelsAsync(
            [("a", 0, ["sk-upstream-a"])],
            WithModels((db, _) => db.Settings.Add(new GatewaySettings { DebugCaptureUntil = InAnHour })));

        using HttpResponseMessage response = await host.PostAsync(
            "{\"model\":\"not-published\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await Task.Delay(300);
        Assert.Empty(await ReadPayloadsAsync(host));
    }
}
