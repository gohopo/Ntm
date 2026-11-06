using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Live;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Relay;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Traffic;
using Ntm.Gateway.Upstream;

namespace Ntm.Gateway;

/// <summary>Gateway 的全部服务。Server 只负责调用这里并映射端点（ADR-0007）。</summary>
public static class GatewayServiceRegistration
{
    public static IServiceCollection AddNtmGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddNtmData(configuration);

        // 密钥环放在数据目录里，和两个库一起备份；丢了就要重新录入所有渠道密钥（ADR-0006）。
        // 目录在构造选项时才解析，不在注册时：测试与部署在注册之后才覆盖 Ntm:Data:Directory 的话，
        // 注册时就读会把密钥环写进错的目录。效果等同 PersistKeysToFileSystem。
        services.AddDataProtection().SetApplicationName("Ntm");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<NtmDataOptions>, ILoggerFactory>((options, data, loggers) =>
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(Path.Combine(data.Value.ResolveDirectory(), "keys")),
                    loggers));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ChannelKeyProtector>();
        services.AddSingleton<ConfigSnapshotProvider>();
        services.AddScoped<AdminAccountService>();

        // 冷却与健康度都是进程内状态（ADR-0003），单例；重启丢掉是可以接受的。
        services.AddSingleton(provider => new CooldownRegistry(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new HealthTracker(provider.GetRequiredService<TimeProvider>()));
        // Restriction 的计数器同样是进程内状态（ADR-0003）。费用计数器启动时从汇总载入（Q32/Q88），
        // RPM 与并发计数重启清零。
        services.AddSingleton<GatewayTimeZone>();
        services.AddSingleton(provider => new CostLedger(
            provider.GetRequiredService<GatewayTimeZone>(),
            provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<RequestRateCounter>();
        services.AddSingleton(new ConcurrencyGauges());
        services.AddSingleton<RestrictionChecker>();
        services.AddSingleton<LiveHub>();
        services.AddSingleton(provider => new InFlightRegistry(provider.GetRequiredService<LiveHub>()));
        services.AddSingleton(provider => new AffinityTable(provider.GetRequiredService<TimeProvider>()));
        // ResponseLanding 落库（Q269）：内存是缓存，后台写入器把改动写进 ntm-traffic.db。
        services.AddSingleton(provider =>
        {
            int days = provider.GetRequiredService<IOptions<RetentionOptions>>().Value.LandingRetentionDays;
            return new ResponseLandingTable(
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                days > 0 ? TimeSpan.FromDays(days) : TimeSpan.MaxValue);
        });
        services.AddSingleton<ResponseLandingWriter>();
        services.AddHostedService(provider => provider.GetRequiredService<ResponseLandingWriter>());
        services.AddSingleton(provider => new CandidateSelector(
            provider.GetRequiredService<CooldownRegistry>(),
            provider.GetRequiredService<HealthTracker>(),
            provider.GetRequiredService<RestrictionChecker>()));
        services.AddSingleton<UpstreamHttpClientFactory>();
        services.AddSingleton(provider => new UpstreamForwarder(
            provider.GetRequiredService<UpstreamHttpClientFactory>(),
            timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<DataDirectoryGuard>();
        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<TrafficRecorder>();
        services.AddHostedService<TrafficWriter>();

        // 过了保留期的请求记录每小时清一次；汇总不受影响（Q42）。
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddSingleton<RetentionCleaner>();
        services.AddHostedService(provider => provider.GetRequiredService<RetentionCleaner>());
        services.AddSingleton<ChatRelay>();
        services.AddSingleton<ModelsCatalog>();
        services.AddSingleton<TokenCounter>();
        services.AddSingleton<Traffic.ResponseUsageBackfill>();
        services.AddSingleton<ResponsesResourceRelay>();
        services.AddSingleton<ThinkingStyleMemory>();
        services.AddSingleton<Traffic.BackgroundUsagePoller>();
        services.AddHostedService(provider => provider.GetRequiredService<Traffic.BackgroundUsagePoller>());

        // 价目目录：单例持有当前目录，后台任务负责刷新（Q69/Q72）。
        services.Configure<PricingOptions>(configuration.GetSection(PricingOptions.SectionName));
        services.AddSingleton<PriceCatalogService>();
        services.AddSingleton<PriceCatalogRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<PriceCatalogRefresher>());

        // 单价行（Q48）：渠道保存后补行，目录刷新后自动行重新匹配。
        services.AddSingleton<PriceRowService>();
        services.AddSingleton<IPriceCatalogListener, PriceRowCatalogListener>();

        // ModelSync（Q135–Q142）：每小时从上游取回开了同步的渠道的模型清单。
        services.Configure<ModelSyncOptions>(configuration.GetSection(ModelSyncOptions.SectionName));
        services.AddSingleton(provider => new ModelListFetcher(provider.GetRequiredService<UpstreamHttpClientFactory>()));
        services.AddSingleton<ModelSyncService>();
        services.AddSingleton<ModelSyncScheduler>();
        services.AddHostedService(provider => provider.GetRequiredService<ModelSyncScheduler>());

        // DebugCapture（Q153–Q161）：原文单独落 ntm-debug.db，队列满了就丢，后台按 24 小时清理。
        services.Configure<Debugging.DebugCaptureOptions>(configuration.GetSection(Debugging.DebugCaptureOptions.SectionName));
        services.AddSingleton<Debugging.DebugCaptureSink>();
        services.AddHostedService(provider => provider.GetRequiredService<Debugging.DebugCaptureSink>());
        services.AddSingleton<Debugging.DebugCleaner>();
        services.AddHostedService(provider => provider.GetRequiredService<Debugging.DebugCleaner>());

        // 停机：先等进行中的请求，等够了中断（Q343）。主机总等待放宽到 40 秒，
        // 给写入器留出落盘时间；配置里写了 shutdownTimeoutSeconds 就听配置的。
        services.AddSingleton<Live.ShutdownDrainer>();
        services.AddHostedService(provider => provider.GetRequiredService<Live.ShutdownDrainer>());
        services.PostConfigure<HostOptions>(options =>
        {
            if (options.ShutdownTimeout == TimeSpan.FromSeconds(30))
            {
                options.ShutdownTimeout = Live.ShutdownDrainer.HostShutdownTimeout;
            }
        });

        return services;
    }

    /// <summary>迁移两个库、首次启动种子、载入配置快照。</summary>
    public static async Task InitializeNtmGatewayAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 先占住数据目录，再动库（Q341）。
        services.GetRequiredService<DataDirectoryGuard>()
            .Hold(services.GetRequiredService<IOptions<NtmDataOptions>>().Value.ResolveDirectory());

        await services.MigrateNtmDatabasesAsync(cancellationToken).ConfigureAwait(false);
        await FirstRunSeeder.SeedAsync(services, configuration, cancellationToken).ConfigureAwait(false);

        // 先补单价行再建快照：快照里带着单价。缓存目录这时读一次，后台任务稍后还会再读一次，
        // 读的是同一个文件，重复一次比让首个请求没有单价划算（Q48/Q72）。
        PriceCatalogService prices = services.GetRequiredService<PriceCatalogService>();
        await prices.LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<PriceRowService>()
            .SyncAsync(prices.Current, cancellationToken)
            .ConfigureAwait(false);

        await services.GetRequiredService<ConfigSnapshotProvider>().ReloadAsync(cancellationToken).ConfigureAwait(false);

        // 费用上限的计数器从汇总表载入（Q88）。放在快照之后、开始接请求之前。
        GatewayTimeZone timeZone = services.GetRequiredService<GatewayTimeZone>();
        if (timeZone.IsFallback)
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(GatewayServiceRegistration))
                .LogTimeZoneFallback(timeZone.Requested ?? "", timeZone.Id);
        }

        await services.GetRequiredService<CostLedger>().LoadAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class GatewayStartupLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "TZ 设为 {Requested}，但没能解析成时区，按 {Zone} 计算每日费用上限与可用时段。镜像里可能缺少 tzdata。")]
    public static partial void LogTimeZoneFallback(this ILogger logger, string requested, string zone);
}
