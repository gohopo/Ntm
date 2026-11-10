using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>价目目录的下载、缓存与失败处理（Q69/Q72）。</summary>
public sealed class PriceCatalogServiceTests
{
    private const string Catalog = """
        {
          "anthropic": { "id": "anthropic", "name": "Anthropic", "models": {
            "claude-sonnet-4-5": { "id": "claude-sonnet-4-5", "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 } } } },
          "openai": { "id": "openai", "name": "OpenAI", "models": {
            "gpt-4.1": { "id": "gpt-4.1", "cost": { "input": 2, "output": 8, "cache_read": 0.5 } } } },
          "google": { "id": "google", "name": "Google", "models": {
            "gemini-2.5-pro": { "id": "gemini-2.5-pro", "cost": { "input": 1.25, "output": 10 } } } }
        }
        """;

    [Fact]
    public async Task ASuccessfulRefreshKeepsOnlyTheDeclaredProvidersAndWritesTheCache()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");

        Assert.True(await h.Service.RefreshAsync(CancellationToken.None));

        PriceCatalog catalog = h.Service.Current;
        Assert.Equal(["anthropic", "google", "openai"], catalog.Providers.Select(p => p.Id));
        Assert.Equal(["anthropic"], catalog.PricedProviders);
        Assert.Equal("\"v1\"", catalog.ETag);
        Assert.Equal(h.Time.GetUtcNow(), catalog.FetchedAt);
        Assert.Null(h.Service.Status.LastError);
        Assert.True(File.Exists(h.CachePath));

        // 第一次没有 ETag 可带。
        Assert.Null(h.Handler.Requests[0].IfNoneMatch);
    }

    [Fact]
    public async Task ARestartReadsTheCacheWithoutTouchingTheNetwork()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        using PriceCatalogService restarted = h.NewService();
        await restarted.LoadCacheAsync(CancellationToken.None);

        Assert.Single(h.Handler.Requests);
        CatalogPrice sonnet = Assert.Single(restarted.Current.PricesOf("anthropic"));
        Assert.Equal(new PriceSet(3m, 0.3m, 3.75m, 6m, 15m), sonnet.Prices);
        Assert.Equal("\"v1\"", restarted.Current.ETag);
        Assert.False(await restarted.NeedsProvidersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACacheFromAnOlderFormatIsUsedButItsETagIsDropped()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        // 加思考输出单价之前写的文件没有 Schema 字段。带着它的 ETag 会拿到 304，新字段就永远补不上。
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(h.CachePath))!.AsObject();
        node.Remove("Schema");
        await File.WriteAllTextAsync(h.CachePath, node.ToJsonString());

        using PriceCatalogService restarted = h.NewService();
        await restarted.LoadCacheAsync(CancellationToken.None);

        Assert.Single(restarted.Current.PricesOf("anthropic"));
        Assert.Null(restarted.Current.ETag);
    }

    [Fact]
    public async Task NotModifiedKeepsThePricesAndMovesTheTimestamp()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        h.Time.Advance(TimeSpan.FromHours(24));
        h.Handler.Respond(HttpStatusCode.NotModified, "", etag: "\"v1\"");
        Assert.True(await h.Service.RefreshAsync(CancellationToken.None));

        Assert.Equal("\"v1\"", h.Handler.Requests[1].IfNoneMatch);
        Assert.Single(h.Service.Current.PricesOf("anthropic"));
        Assert.Equal(h.Time.GetUtcNow(), h.Service.Current.FetchedAt);
    }

    [Fact]
    public async Task AFailureKeepsTheOldCatalogAndReportsWhy()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);
        DateTimeOffset? fetched = h.Service.Current.FetchedAt;

        h.Time.Advance(TimeSpan.FromHours(24));
        h.Handler.Respond(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        Assert.False(await h.Service.RefreshAsync(CancellationToken.None));

        Assert.Single(h.Service.Current.PricesOf("anthropic"));
        Assert.Equal(fetched, h.Service.Status.FetchedAt);
        Assert.Equal(h.Time.GetUtcNow(), h.Service.Status.LastAttemptAt);
        Assert.Equal("目录服务返回 HTTP 502。", h.Service.Status.LastError);

        // 不是 JSON 也只是失败，不是崩溃。
        h.Handler.Respond(HttpStatusCode.OK, "{ not json");
        Assert.False(await h.Service.RefreshAsync(CancellationToken.None));
        Assert.Equal("目录内容不是合法的 JSON。", h.Service.Status.LastError);
        Assert.Single(h.Service.Current.PricesOf("anthropic"));
    }

    [Fact]
    public async Task ANewlyDeclaredProviderForcesAFullDownload()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        await h.AddChannelAsync("openai");
        Assert.True(await h.Service.NeedsProvidersAsync(CancellationToken.None));

        // 带着旧 ETag 会拿到 304，就永远取不到 openai 的单价，所以要丢掉 ETag。
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        Assert.Null(h.Handler.Requests[1].IfNoneMatch);
        Assert.Equal(["anthropic", "openai"], h.Service.Current.PricedProviders.Order());
        Assert.False(await h.Service.NeedsProvidersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AProviderMissingFromTheCatalogDoesNotCauseEndlessRedownloads()
    {
        await using Harness h = await Harness.StartAsync("volcengine");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        Assert.Empty(h.Service.Current.PricedProviders);
        Assert.False(await h.Service.NeedsProvidersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListenersHearAboutEveryNewCatalog()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        await h.Service.RefreshAsync(CancellationToken.None);

        PriceCatalog heard = Assert.Single(h.Listener.Heard);
        Assert.Same(h.Service.Current, heard);
    }

    [Theory]
    [InlineData("accurate")]
    [InlineData("missing")]
    [InlineData("lying")]
    public async Task OversizedCatalogKeepsOldPricesCacheAndTimestampRegardlessOfContentLength(string lengthMode)
    {
        const int limit = 32 * 1024 * 1024;
        Assert.Equal(limit, new PricingOptions().MaxCatalogBytes);
        await using Harness h = await Harness.StartAsync("anthropic");
        h.Handler.Respond(HttpStatusCode.OK, Catalog, etag: "\"v1\"");
        Assert.True(await h.Service.RefreshAsync(CancellationToken.None));
        PriceCatalog old = h.Service.Current;
        string oldCache = await File.ReadAllTextAsync(h.CachePath);
        h.Time.Advance(TimeSpan.FromHours(1));

        // Leading JSON whitespace keeps the entire response valid JSON, but requires the parser to read past the cap.
        byte[] body = new byte[limit + Encoding.UTF8.GetByteCount(Catalog)];
        Array.Fill(body, (byte)' ', 0, limit);
        Encoding.UTF8.GetBytes(Catalog, body.AsSpan(limit));
        h.Handler.RespondContent(() =>
        {
            var content = new UnknownLengthContent(body);
            content.Headers.ContentLength = lengthMode switch
            {
                "accurate" => body.Length,
                "lying" => 1,
                _ => null,
            };
            Assert.Equal(lengthMode == "missing", content.Headers.ContentLength is null);
            return content;
        });

        Assert.False(await h.Service.RefreshAsync(CancellationToken.None));
        Assert.Same(old, h.Service.Current);
        Assert.Equal(old.FetchedAt, h.Service.Status.FetchedAt);
        Assert.Equal(h.Time.GetUtcNow(), h.Service.Status.LastAttemptAt);
        Assert.Equal("目录超过了 32 MiB 的上限，没有采用。", h.Service.Status.LastError);
        Assert.Equal(oldCache, await File.ReadAllTextAsync(h.CachePath));
        Assert.Single(h.Listener.Heard);
    }

    [Fact]
    public async Task CatalogExactlyAtConfiguredLimitIsAccepted()
    {
        await using Harness h = await Harness.StartAsync("anthropic");
        int size = Encoding.UTF8.GetByteCount(Catalog);
        using PriceCatalogService service = h.NewService(new PricingOptions { MaxCatalogBytes = size });
        h.Handler.RespondContent(() => new UnknownLengthContent(Encoding.UTF8.GetBytes(Catalog)));
        Assert.True(await service.RefreshAsync(CancellationToken.None));
        Assert.Single(service.Current.PricesOf("anthropic"));
    }

    /// <summary>A streamed response with no computable length, even though the fixture lives in memory.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class RecordingListener : IPriceCatalogListener
    {
        public List<PriceCatalog> Heard { get; } = [];

        public Task OnCatalogChangedAsync(PriceCatalog catalog, CancellationToken cancellationToken)
        {
            Heard.Add(catalog);
            return Task.CompletedTask;
        }
    }

    private sealed record SeenRequest(Uri? Uri, string? IfNoneMatch);

    /// <summary>脚本化的响应，加上收到的请求。多个服务实例共享一份。</summary>
    private sealed class Script
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<SeenRequest> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body, string? etag = null) =>
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (etag is not null)
                {
                    response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
                }

                return response;
            });

        public void RespondContent(Func<HttpContent> content) =>
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });

        public HttpResponseMessage Next(HttpRequestMessage request)
        {
            Requests.Add(new SeenRequest(request.RequestUri, request.Headers.IfNoneMatch.FirstOrDefault()?.ToString()));
            return _responses.Dequeue()();
        }
    }

    /// <summary>每个服务实例自己一个处理器（服务 dispose 时会 dispose 它），脚本是共享的。</summary>
    private sealed class ScriptedHandler(Script script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(script.Next(request));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _directory;
        private readonly List<PriceCatalogService> _owned = [];

        private Harness(SqliteConnection connection, ServiceProvider services, string directory)
        {
            _connection = connection;
            _services = services;
            _directory = directory;
            Service = NewService();
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));

        public Script Handler { get; } = new();

        public RecordingListener Listener => (RecordingListener)_services.GetRequiredService<IPriceCatalogListener>();

        public PriceCatalogService Service { get; }

        public string CachePath => Path.Combine(_directory, new PricingOptions().CacheFileName);

        public static async Task<Harness> StartAsync(params string[] providers)
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<ConfigDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IPriceCatalogListener, RecordingListener>();
            ServiceProvider provider = services.BuildServiceProvider();

            string directory = Path.Combine(Path.GetTempPath(), "ntm-price-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var harness = new Harness(connection, provider, directory);
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Database.MigrateAsync();
            }

            foreach (string name in providers)
            {
                await harness.AddChannelAsync(name);
            }

            return harness;
        }

        public PriceCatalogService NewService(PricingOptions? options = null)
        {
            var service = new PriceCatalogService(
                _services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options ?? new PricingOptions()),
                Options.Create(new NtmDataOptions { Directory = _directory }),
                Time,
                NullLogger<PriceCatalogService>.Instance,
                new ScriptedHandler(Handler));
            _owned.Add(service);
            return service;
        }

        public async Task AddChannelAsync(string provider)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            db.Channels.Add(new Channel
            {
                Name = provider + "-" + Guid.NewGuid().ToString("N")[..6],
                BaseUrl = "https://" + provider + ".example",
                Provider = provider,
                CreatedAt = Time.GetUtcNow(),
                UpdatedAt = Time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (PriceCatalogService service in _owned)
            {
                service.Dispose();
            }

            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
