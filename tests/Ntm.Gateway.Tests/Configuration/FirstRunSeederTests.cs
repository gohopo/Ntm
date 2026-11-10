using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Security;

namespace Ntm.Gateway.Tests.Configuration;

public sealed class FirstRunSeederTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstBootAndRestartNeverCreateDefaultApiKeys(bool seedChannel)
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var values = new Dictionary<string, string?>
        {
            [AdminAccountService.InitialPasswordVariable] = "correct-horse-battery",
        };
        if (seedChannel)
        {
            values["Ntm:Seed:BaseUrl"] = "https://upstream.example";
            values["Ntm:Seed:ChannelKey"] = "sk-upstream-secret";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<ChannelKeyProtector>();
        services.AddScoped<AdminAccountService>();
        services.AddDbContext<ConfigDbContext>(options => options.UseSqlite(connection));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().Database.MigrateAsync();
        }

        await FirstRunSeeder.SeedAsync(provider, configuration);
        await FirstRunSeeder.SeedAsync(provider, configuration);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Assert.Equal(1, await db.AdminAccounts.CountAsync());
            Assert.Equal(seedChannel ? 1 : 0, await db.Channels.CountAsync());
            Assert.Equal(seedChannel ? 1 : 0, await db.ChannelKeys.CountAsync());
            Assert.Empty(await db.ApiKeys.ToListAsync());
            db.ApiKeys.Add(new ApiKey
            {
                Name = "existing",
                Fingerprint = SecretFingerprint.Compute("ntm-existing-client-secret"),
                Prefix = "ntm-existing",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await FirstRunSeeder.SeedAsync(provider, configuration);
        await using AsyncServiceScope read = provider.CreateAsyncScope();
        ApiKey existing = await read.ServiceProvider.GetRequiredService<ConfigDbContext>().ApiKeys.SingleAsync();
        Assert.Equal("existing", existing.Name);
        Assert.Equal(SecretFingerprint.Compute("ntm-existing-client-secret"), existing.Fingerprint);
    }
}
