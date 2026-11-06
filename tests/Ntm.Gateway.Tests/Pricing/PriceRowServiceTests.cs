using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Pricing;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>单价行的生成与重新匹配（Q48/Q69）。</summary>
public sealed class PriceRowServiceTests
{
    private static readonly PriceSet SonnetPrices = new(3m, 0.3m, 3.75m, 6m, 15m);

    [Fact]
    public async Task EveryProviderModelPairGetsARowAndTheCatalogFillsThePrices()
    {
        await using Harness h = await Harness.StartAsync();
        await h.AddChannelAsync("anthropic", "claude-sonnet-4-5-20250929", "claude-haiku-4-5");
        await h.AddChannelAsync("openai", "gpt-4.1");

        Assert.Equal(3, await h.Service.SyncAsync(h.Catalog(), CancellationToken.None));

        List<PriceRow> rows = await h.RowsAsync();
        Assert.Equal(
            ["anthropic/claude-haiku-4-5", "anthropic/claude-sonnet-4-5-20250929", "openai/gpt-4.1"],
            rows.Select(r => r.Provider + "/" + r.UpstreamModel));

        // 带日期的名字也能配上目录里的 claude-sonnet-4-5（Q68 的最长连续段规则）。
        PriceRow sonnet = rows.Single(r => r.UpstreamModel.StartsWith("claude-sonnet", StringComparison.Ordinal));
        Assert.Equal(PriceSource.Auto, sonnet.Source);
        Assert.Equal("claude-sonnet-4-5", sonnet.MatchedModelId);
        Assert.Equal(SonnetPrices, sonnet.ToPriceSet());

        // 目录里没有 claude-haiku-4-5：行建出来但没有单价。
        PriceRow haiku = rows.Single(r => r.UpstreamModel == "claude-haiku-4-5");
        Assert.True(haiku.IsEmpty);
        Assert.Null(haiku.MatchedModelId);

        // 长上下文单价跟着行一起存。
        PriceRow gpt = rows.Single(r => r.Provider == "openai");
        Assert.Equal([272_000], gpt.ToLongContext().Select(g => g.InputTokensOver));
        Assert.Equal(new PriceSet(4m, 0.5m, null, null, 16m), gpt.ToLongContext()[0].Prices);

        // 再同步一次不会重复建行。
        Assert.Equal(0, await h.Service.SyncAsync(h.Catalog(), CancellationToken.None));
    }

    [Fact]
    public async Task RowsSurviveTheUpstreamModelBeingRemoved()
    {
        await using Harness h = await Harness.StartAsync();
        Channel channel = await h.AddChannelAsync("anthropic", "claude-sonnet-4-5");
        await h.Service.SyncAsync(h.Catalog(), CancellationToken.None);

        await h.RemoveModelsAsync(channel.Id);
        Assert.Equal(0, await h.Service.SyncAsync(h.Catalog(), CancellationToken.None));

        PriceRow row = Assert.Single(await h.RowsAsync());
        Assert.Equal("claude-sonnet-4-5", row.UpstreamModel);
        Assert.Equal(SonnetPrices, row.ToPriceSet());
    }

    [Fact]
    public async Task ChannelsWithoutAProviderGetNoRows()
    {
        await using Harness h = await Harness.StartAsync();
        await h.AddChannelAsync("", "some-model");

        Assert.Equal(0, await h.Service.SyncAsync(h.Catalog(), CancellationToken.None));
        Assert.Empty(await h.RowsAsync());
    }

    [Fact]
    public async Task ARefreshUpdatesAutoRowsAndLeavesManualOnesAlone()
    {
        await using Harness h = await Harness.StartAsync();
        await h.AddChannelAsync("anthropic", "claude-sonnet-4-5", "claude-haiku-4-5");
        await h.Service.SyncAsync(h.Catalog(), CancellationToken.None);

        // 人工改过 haiku：刷新不许动它。
        await h.EditAsync("anthropic", "claude-haiku-4-5", row =>
        {
            row.Source = PriceSource.Manual;
            row.InputPrice = 1m;
            row.OutputPrice = 5m;
        });

        h.Time.Advance(TimeSpan.FromHours(1));
        await h.Service.RematchAsync(
            h.Catalog(sonnetInput: 4m, haiku: new PriceSet(9m, null, null, null, 9m)),
            CancellationToken.None);

        List<PriceRow> rows = await h.RowsAsync();
        PriceRow sonnet = rows.Single(r => r.UpstreamModel == "claude-sonnet-4-5");
        Assert.Equal(4m, sonnet.InputPrice);
        Assert.Equal(h.Time.GetUtcNow(), sonnet.UpdatedAt);

        PriceRow haiku = rows.Single(r => r.UpstreamModel == "claude-haiku-4-5");
        Assert.Equal(1m, haiku.InputPrice);
        Assert.Equal(5m, haiku.OutputPrice);
    }

    [Fact]
    public async Task AModelThatLeavesTheCatalogLosesItsAutoPriceButAMissingProviderDoesNot()
    {
        await using Harness h = await Harness.StartAsync();
        await h.AddChannelAsync("anthropic", "claude-sonnet-4-5");
        await h.AddChannelAsync("openai", "gpt-4.1");
        await h.Service.SyncAsync(h.Catalog(), CancellationToken.None);

        // 新目录里 anthropic 还在但没有这个模型，openai 整个供应商都没取到。
        var narrowed = new PriceCatalog(
            [new CatalogProvider("anthropic", "Anthropic", 1)],
            [new CatalogPrice("anthropic", "claude-opus-4-5", SonnetPrices, CatalogPrice.NoLongContext)],
            "\"v2\"",
            h.Time.GetUtcNow());

        await h.Service.RematchAsync(narrowed, CancellationToken.None);

        List<PriceRow> rows = await h.RowsAsync();
        PriceRow sonnet = rows.Single(r => r.Provider == "anthropic");
        Assert.True(sonnet.IsEmpty);
        Assert.Null(sonnet.MatchedModelId);

        // 这次没取到 openai 的单价，不代表它免费：留着上次的。
        PriceRow gpt = rows.Single(r => r.Provider == "openai");
        Assert.Equal(2m, gpt.InputPrice);
        Assert.Equal("gpt-4.1", gpt.MatchedModelId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private Harness(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            _services = services;
            Service = new PriceRowService(
                services.GetRequiredService<IServiceScopeFactory>(),
                Time,
                NullLogger<PriceRowService>.Instance);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));

        public PriceRowService Service { get; }

        public static async Task<Harness> StartAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<ConfigDbContext>(options => options.UseSqlite(connection));
            ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Database.MigrateAsync();
            }

            return new Harness(connection, provider);
        }

        /// <summary>目录：sonnet、gpt-4.1（带一组长上下文单价），可选再给 haiku 一个单价。</summary>
        public PriceCatalog Catalog(decimal sonnetInput = 3m, PriceSet? haiku = null)
        {
            List<CatalogPrice> prices =
            [
                new("anthropic", "claude-sonnet-4-5", SonnetPrices with { Input = sonnetInput }, CatalogPrice.NoLongContext),
                new(
                    "openai",
                    "gpt-4.1",
                    new PriceSet(2m, 0.5m, null, null, 8m),
                    [new LongContextPrice(272_000, new PriceSet(4m, 0.5m, null, null, 16m))]),
            ];

            if (haiku is not null)
            {
                prices.Add(new CatalogPrice("anthropic", "claude-haiku-4-5", haiku, CatalogPrice.NoLongContext));
            }

            return new PriceCatalog(
                [new CatalogProvider("anthropic", "Anthropic", 1), new CatalogProvider("openai", "OpenAI", 1)],
                prices,
                "\"v1\"",
                Time.GetUtcNow());
        }

        public async Task<Channel> AddChannelAsync(string provider, params string[] models)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var channel = new Channel
            {
                Name = (provider.Length == 0 ? "unlisted" : provider) + "-" + Guid.NewGuid().ToString("N")[..6],
                BaseUrl = "https://example.test",
                Provider = provider,
                CreatedAt = Time.GetUtcNow(),
                UpdatedAt = Time.GetUtcNow(),
            };
            foreach (string model in models)
            {
                channel.UpstreamModels.Add(new UpstreamModel
                {
                    Name = model,
                    Source = UpstreamModelSource.Manual,
                    CreatedAt = Time.GetUtcNow(),
                    LastSeenAt = Time.GetUtcNow(),
                });
            }

            db.Channels.Add(channel);
            await db.SaveChangesAsync();
            return channel;
        }

        public async Task RemoveModelsAsync(int channelId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Channel channel = await db.Channels
                .Include(c => c.UpstreamModels)
                .SingleAsync(c => c.Id == channelId);
            channel.UpstreamModels.Clear();
            await db.SaveChangesAsync();
        }

        public async Task EditAsync(string provider, string model, Action<PriceRow> edit)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            PriceRow row = await db.PriceRows.SingleAsync(r => r.Provider == provider && r.UpstreamModel == model);
            edit(row);
            await db.SaveChangesAsync();
        }

        public async Task<List<PriceRow>> RowsAsync()
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            return await db.PriceRows
                .OrderBy(r => r.Provider)
                .ThenBy(r => r.UpstreamModel)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
