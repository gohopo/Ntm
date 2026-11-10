using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ntm.Gateway.Data;

/// <summary>注册三个库（ADR-0004、Q153）。</summary>
public static class GatewayDataRegistration
{
    public static IServiceCollection AddNtmData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<NtmDataOptions>(configuration.GetSection(NtmDataOptions.SectionName));
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddSingleton<Audit.AuditSaveChangesInterceptor>();

        services.AddDbContext<ConfigDbContext>((provider, options) =>
        {
            var dataOptions = provider.GetRequiredService<IOptions<NtmDataOptions>>().Value;
            EnsureDirectory(dataOptions);
            options.UseSqlite(
                dataOptions.ConfigConnectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(ConfigDbContext).Assembly.FullName));
            options.AddInterceptors(
                provider.GetRequiredService<SqlitePragmaInterceptor>(),
                provider.GetRequiredService<Audit.AuditSaveChangesInterceptor>());
        });

        services.AddDbContext<TrafficDbContext>((provider, options) =>
        {
            var dataOptions = provider.GetRequiredService<IOptions<NtmDataOptions>>().Value;
            EnsureDirectory(dataOptions);
            options.UseSqlite(
                dataOptions.TrafficConnectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(TrafficDbContext).Assembly.FullName));
            options.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>());
        });

        services.AddDbContext<DebugDbContext>((provider, options) =>
        {
            var dataOptions = provider.GetRequiredService<IOptions<NtmDataOptions>>().Value;
            EnsureDirectory(dataOptions);
            options.UseSqlite(
                dataOptions.DebugConnectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(DebugDbContext).Assembly.FullName));
            options.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>());
        });

        return services;
    }

    /// <summary>把三个库的迁移都应用上。</summary>
    public static async Task MigrateNtmDatabasesAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();

        ConfigDbContext config = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        TrafficDbContext traffic = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();
        DebugDbContext debug = scope.ServiceProvider.GetRequiredService<DebugDbContext>();

        // 先三个库都查完再迁移：不要出现一个库已经升级、另一个库拒绝启动的半截状态。
        await RefuseDowngradeAsync(config, "配置库", cancellationToken).ConfigureAwait(false);
        await RefuseDowngradeAsync(traffic, "请求记录库", cancellationToken).ConfigureAwait(false);
        await RefuseDowngradeAsync(debug, "调试库", cancellationToken).ConfigureAwait(false);

        await config.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await traffic.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await debug.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await EnableIncrementalVacuumAsync(debug, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 库里有程序不认识的迁移，说明库是更新的版本写的（降级了）。照样跑会在用到新列时出错，
    /// 所以直接拒绝启动，让管理员换回新版本或从备份恢复（Q342）。
    /// </summary>
    private static async Task RefuseDowngradeAsync(DbContext context, string label, CancellationToken cancellationToken)
    {
        IEnumerable<string> applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        string[] unknown = [.. applied.Where(id => !known.Contains(id))];
        if (unknown.Length > 0)
        {
            throw new NtmStartupException(
                $"{label}是更新的版本写的，这个版本的网关不认识它的迁移：{string.Join("、", unknown)}。"
                + "请换回原来的版本，或者停掉网关后用 restore 恢复升级前的备份。");
        }
    }

    /// <summary>
    /// 调试库要反复删掉大量行，所以要能把空闲页还给文件系统。auto_vacuum 必须在建表前设好，
    /// 而迁移已经建过表了，只能用一次 VACUUM 把模式换过来——所以先查一下，已经是增量模式就不动（Q159）。
    /// </summary>
    private static async Task EnableIncrementalVacuumAsync(DebugDbContext debug, CancellationToken cancellationToken)
    {
        System.Data.Common.DbConnection connection = debug.Database.GetDbConnection();
        await debug.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using System.Data.Common.DbCommand read = connection.CreateCommand();
            read.CommandText = "PRAGMA auto_vacuum;";
            object? mode = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt64(mode, System.Globalization.CultureInfo.InvariantCulture) == 2)
            {
                return;
            }

            await using System.Data.Common.DbCommand write = connection.CreateCommand();
            write.CommandText = "PRAGMA auto_vacuum = INCREMENTAL; VACUUM;";
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await debug.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureDirectory(NtmDataOptions options)
    {
        System.IO.Directory.CreateDirectory(options.ResolveDirectory());
    }
}
