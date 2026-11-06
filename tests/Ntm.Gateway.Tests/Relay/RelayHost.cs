using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Security;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Relay;

/// <summary>
/// 跑一个真实的 ASP.NET 管道（TestServer）+ 真实的 SQLite 两个库，只把上游换成 <see cref="ScriptedUpstream"/>。
/// 记录要等写入器在停机时刷完才能读，所以断言记录之前先 <see cref="StopAsync"/>。
/// </summary>
internal sealed class RelayHost : IAsyncDisposable
{
    public const string ApiKeySecret = "ntm-test-token-0123456789";

    /// <summary>种子里下发的模型名。每个渠道都以同名的 UpstreamModel 提供它们。</summary>
    public static readonly string[] Models = ["gpt-4o", "gpt-4o-2024-11-20", "claude-x"];

    private readonly WebApplication _app;
    private readonly string _directory;
    private bool _stopped;

    private RelayHost(WebApplication app, string directory, ScriptedUpstream upstream)
    {
        _app = app;
        _directory = directory;
        Upstream = upstream;
        Client = app.GetTestClient();
    }

    public ScriptedUpstream Upstream { get; }

    /// <summary>按收到响应的先后记下每个请求的 ID（响应头 x-ntm-request-id），给同一毫秒内的记录定序。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _responseOrder = new();

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>按 OpenAI 或 Anthropic 客户端的习惯取模型列表。</summary>
    public async Task<HttpResponseMessage> GetModelsAsync(bool anthropic = false, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        if (token is not null)
        {
            if (anthropic)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", token);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }
        }

        return await Client.SendAsync(request);
    }

    /// <param name="channels">每个渠道：名字、优先级、密钥原文（按顺序作为密钥优先级从高到低）。</param>
    public static Task<RelayHost> StartAsync(params (string Name, int Priority, string[] Secrets)[] channels) =>
        StartWithFormatsAsync([.. channels.Select(c => (c.Name, c.Priority, c.Secrets, ApiFormat.OpenAiChat))]);

    /// <param name="channels">同上，另外指定渠道的上游 ApiFormat。</param>
    public static Task<RelayHost> StartWithFormatsAsync(params (string Name, int Priority, string[] Secrets, ApiFormat Format)[] channels) =>
        StartCoreAsync(channels, null, null);

    /// <summary>
    /// 自己决定下发什么模型、各渠道提供什么上游模型。<paramref name="configure"/> 在渠道与 API 密钥种好之后、
    /// 保存之前运行，拿到的是同一个 <see cref="ConfigDbContext"/>。
    /// </summary>
    /// <param name="timeouts">转发超时；null 用默认值。</param>
    public static Task<RelayHost> StartWithModelsAsync(
        (string Name, int Priority, string[] Secrets)[] channels,
        Action<ConfigDbContext, IReadOnlyList<Channel>> configure,
        ForwardTimeouts? timeouts = null) =>
        StartCoreAsync(
            [.. channels.Select(c => (c.Name, c.Priority, c.Secrets, ApiFormat.OpenAiChat))],
            configure,
            timeouts);

    private static async Task<RelayHost> StartCoreAsync(
        (string Name, int Priority, string[] Secrets, ApiFormat Format)[] channels,
        Action<ConfigDbContext, IReadOnlyList<Channel>>? configure,
        ForwardTimeouts? timeouts)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ntm-relay-" + Guid.NewGuid().ToString("N"));
        var upstream = new ScriptedUpstream();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [NtmDataOptions.SectionName + ":Directory"] = directory,

            // 测试不许连外网：关掉价目目录的自动刷新。
            ["Ntm:Pricing:RefreshIntervalHours"] = "0",
            ["Ntm:ModelSync:IntervalMinutes"] = "0",
        });

        builder.Services.AddNtmGateway(builder.Configuration);
        builder.Services.AddSingleton(provider => new UpstreamForwarder(
            new HttpMessageInvoker(upstream),
            timeouts,
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(new ModelListFetcher(new HttpMessageInvoker(upstream)));

        WebApplication app = builder.Build();
        app.MapPost(
            "/v1/chat/completions",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.OpenAiChat));
        app.MapPost(
            "/v1/messages",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Anthropic));
        app.MapPost(
            "/v1/responses",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.OpenAiResponses));
        app.MapResponsesCompanionEndpoints();
        app.MapPost(
            "/v1/messages/count_tokens",
            (HttpContext context, TokenCounter counter) => counter.HandleAsync(context));
        app.MapPost(
            "/v1/embeddings",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Embeddings));
        app.MapPost(
            "/v1/images/generations",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Images));

        // Workbench 的两个入口（Q200）。真实服务里在 /api/workbench 下、要管理员登录；这里不鉴权。
        app.MapPost(
            "/workbench/channels/{id:int}/send",
            async (int id, int? key, string? kind, HttpContext context, ConfigSnapshotProvider snapshots, ChatRelay relay) =>
            {
                ChannelSnapshot? channel = await snapshots.LoadChannelAsync(id, key);
                if (channel is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                InboundDialect dialect = (kind, channel.ApiFormat) switch
                {
                    ("embedding", _) => InboundDialect.Embeddings,
                    ("image", _) => InboundDialect.Images,
                    (_, ApiFormat.Anthropic) => InboundDialect.Anthropic,
                    (_, ApiFormat.OpenAiResponses) => InboundDialect.OpenAiResponses,
                    _ => InboundDialect.OpenAiChat,
                };
                await relay.HandleWorkbenchAsync(context, dialect, channel);
            });
        app.MapPost(
            "/workbench/models/send",
            (string? format, HttpContext context, ChatRelay relay) => relay.HandleWorkbenchAsync(
                context,
                format == "anthropic" ? InboundDialect.Anthropic
                    : format == "openAiResponses" ? InboundDialect.OpenAiResponses
                    : InboundDialect.OpenAiChat,
                pinned: null));

        app.MapGet(
            "/v1/models",
            (HttpContext context, ModelsCatalog catalog) => catalog.HandleAsync(
                context,
                ModelsCatalog.DialectFor(context.Request)));

        await app.Services.MigrateNtmDatabasesAsync();
        await SeedAsync(app.Services, channels, configure);
        await app.Services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync();
        await app.StartAsync();

        return new RelayHost(app, directory, upstream);
    }

    public async Task<HttpResponseMessage> PostAsync(string json, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return Remember(await Client.SendAsync(request));
    }

    /// <summary>按 Anthropic 客户端的习惯发：<c>x-api-key</c> + <c>anthropic-version</c>。</summary>
    public async Task<HttpResponseMessage> PostMessagesAsync(string json, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", token);
        }

        return Remember(await Client.SendAsync(request));
    }

    /// <summary>按 OpenAI Responses 客户端的习惯发：<c>POST /v1/responses</c> + <c>Authorization: Bearer</c>。</summary>
    public async Task<HttpResponseMessage> PostResponsesAsync(string json, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return Remember(await Client.SendAsync(request));
    }

    /// <summary>OpenAI 客户端的习惯发到任意端点（嵌入、图片生成）：<c>Authorization: Bearer</c>。</summary>
    /// <summary>任意方法、任意路径；<paramref name="json"/> 为 null 时不带请求体。</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostToAsync(string path, string json, string? token = ApiKeySecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return Remember(await Client.SendAsync(request));
    }

    /// <summary>Workbench 的请求：不带任何 API 密钥。</summary>
    public async Task<HttpResponseMessage> PostWorkbenchAsync(string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        return Remember(await Client.SendAsync(request));
    }

    private HttpResponseMessage Remember(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ntm-request-id", out IEnumerable<string>? ids)
            && Guid.TryParse(ids.First(), out Guid id))
        {
            _responseOrder.Enqueue(id);
        }

        return response;
    }

    /// <summary>停机让写入器把队列刷进库，然后读出所有记录。</summary>
    public async Task<List<RequestRecord>> ReadRecordsAsync()
    {
        await StopAsync();

        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        // StartedAt 只到毫秒。Guid v7 在同一毫秒内后面的位是随机的，不能拿来定序；
        // 同一毫秒内按测试收到响应的先后排（顺序发的请求就是发送顺序）。
        Dictionary<Guid, int> arrival = _responseOrder
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        List<RequestRecord> records = [.. (await db.Requests.Include(r => r.Attempts).ToListAsync())
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => arrival.TryGetValue(r.Id, out int index) ? index : int.MaxValue)
            .ThenBy(r => r.Id)];

        foreach (RequestRecord record in records)
        {
            record.Attempts.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        }

        return records;
    }

    /// <summary>停机让写入器把队列刷进库，然后读出所有汇总行。</summary>
    public async Task<List<HourlyAggregate>> ReadAggregatesAsync()
    {
        await StopAsync();

        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        return await db.HourlyAggregates.OrderBy(a => a.ChannelId).ThenBy(a => a.UpstreamModel).ToListAsync();
    }

    private async Task StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            await _app.StopAsync();
        }
    }

    private static async Task SeedAsync(
        IServiceProvider services,
        (string Name, int Priority, string[] Secrets, ApiFormat Format)[] channels,
        Action<ConfigDbContext, IReadOnlyList<Channel>>? configure)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        ChannelKeyProtector protector = scope.ServiceProvider.GetRequiredService<ChannelKeyProtector>();
        List<Channel> created = [];

        foreach ((string name, int priority, string[] secrets, ApiFormat format) in channels)
        {
            var channel = new Channel
            {
                Name = name,
                BaseUrl = $"https://{name}.example",
                ApiFormat = format,
                Priority = priority,
            };

            for (int i = 0; i < secrets.Length; i++)
            {
                channel.Keys.Add(new ChannelKey
                {
                    Name = "key" + i,
                    ProtectedSecret = protector.Protect(secrets[i]),
                    SecretFingerprint = SecretFingerprint.Compute(secrets[i]),
                    SecretPrefix = SecretFingerprint.Prefix(secrets[i]),

                    // 数组里越靠前优先级越高。
                    Priority = secrets.Length - i,
                });
            }

            if (configure is null)
            {
                // 默认：每个渠道都提供这几个上游模型，名字和下发名一致，于是上游收到的 model 不变。
                foreach (string model in Models)
                {
                    channel.UpstreamModels.Add(new UpstreamModel { Name = model, Source = UpstreamModelSource.Manual });
                }
            }

            created.Add(channel);
            db.Channels.Add(channel);
        }

        if (configure is null)
        {
            // 每个名字一个已启用的 PublishedModel，一条「所有渠道 + 精确名」的规则。
            foreach (string model in Models)
            {
                var published = new PublishedModel { Name = model, Enabled = true };
                published.Rules.Add(new MatchRule { Ordinal = 1, Kind = MatchKind.Exact, Pattern = model });
                db.PublishedModels.Add(published);
            }
        }

        db.ApiKeys.Add(new ApiKey
        {
            Name = "test",
            Fingerprint = SecretFingerprint.Compute(ApiKeySecret),
            Prefix = SecretFingerprint.Prefix(ApiKeySecret),
        });

        await db.SaveChangesAsync();

        // 先存一次，渠道才有 Id，规则才能按渠道收窄。
        if (configure is not null)
        {
            configure(db, created);
            await db.SaveChangesAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录，删不掉不影响结果。
        }
    }
}
