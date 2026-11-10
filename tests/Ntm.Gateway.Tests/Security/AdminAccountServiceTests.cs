using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Ntm.Gateway.Data;
using Ntm.Gateway.Security;

namespace Ntm.Gateway.Tests.Security;

public sealed class AdminAccountServiceTests
{
    private const string Configured = "correct-horse-battery";

    [Fact]
    public async Task ConfiguredPasswordCreatesTheAccountWithoutPrintingAnything()
    {
        await using Harness h = await Harness.StartAsync(Configured);

        Assert.Null(await h.Service.EnsureCreatedAsync());

        Assert.Equal(1, await h.Service.VerifyAsync(Configured));
        Assert.Null(await h.Service.VerifyAsync("wrong-password-123"));
    }

    [Fact]
    public async Task WithoutConfiguredPasswordAStrongOneIsGeneratedOnce()
    {
        await using Harness h = await Harness.StartAsync(password: null);

        string? generated = await h.Service.EnsureCreatedAsync();

        Assert.NotNull(generated);
        Assert.Equal(20, generated.Length);
        Assert.All(generated, c => Assert.True(char.IsAsciiLetterOrDigit(c)));

        // 容易抄错的字符不出现。
        Assert.DoesNotContain('0', generated);
        Assert.DoesNotContain('O', generated);
        Assert.DoesNotContain('1', generated);
        Assert.DoesNotContain('l', generated);
        Assert.DoesNotContain('I', generated);
        Assert.Equal(1, await h.Service.VerifyAsync(generated));
    }

    [Fact]
    public async Task ASecondStartNeitherRegeneratesNorRereadsTheVariable()
    {
        await using Harness first = await Harness.StartAsync(Configured);
        await first.Service.EnsureCreatedAsync();

        // 同一个库、环境变量已经改成别的值：不能把密码改回去。
        AdminAccountService restarted = first.NewService("a-different-password");
        Assert.Null(await restarted.EnsureCreatedAsync());

        Assert.Equal(1, await restarted.VerifyAsync(Configured));
        Assert.Null(await restarted.VerifyAsync("a-different-password"));
    }

    [Fact]
    public async Task ATooShortConfiguredPasswordRefusesToStart()
    {
        await using Harness h = await Harness.StartAsync("short-11chr");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.EnsureCreatedAsync());

        Assert.Contains("NTM_ADMIN_PASSWORD", error.Message, StringComparison.Ordinal);
        Assert.Null(await h.Service.GetSessionVersionAsync());
    }

    [Fact]
    public async Task TwelveCharactersIsEnough()
    {
        await using Harness h = await Harness.StartAsync("exactly-12ch");

        Assert.Null(await h.Service.EnsureCreatedAsync());
        Assert.Equal(1, await h.Service.VerifyAsync("exactly-12ch"));
    }

    [Fact]
    public async Task WrongCurrentPasswordChangesNothing()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();

        PasswordChangeResult result = await h.Service.ChangePasswordAsync("not-the-password", "a-brand-new-password");

        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword, result.Status);
        Assert.Equal(1, await h.Service.VerifyAsync(Configured));
        Assert.Null(await h.Service.VerifyAsync("a-brand-new-password"));
    }

    [Fact]
    public async Task ATooShortNewPasswordIsRejectedAfterTheCurrentOneChecksOut()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();

        PasswordChangeResult result = await h.Service.ChangePasswordAsync(Configured, "short");

        Assert.Equal(PasswordChangeStatus.TooShort, result.Status);
        Assert.Equal(1, await h.Service.GetSessionVersionAsync());
        Assert.Equal(1, await h.Service.VerifyAsync(Configured));
    }

    [Fact]
    public async Task ChangingThePasswordBumpsTheSessionVersion()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();
        h.Time.Advance(TimeSpan.FromDays(3));

        PasswordChangeResult result = await h.Service.ChangePasswordAsync(Configured, "a-brand-new-password");

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.Equal(2, result.SessionVersion);

        // 换一个 DbContext 读，确认真的落了库，而不只是改了跟踪中的实体。
        AdminAccountService fresh = h.NewService(Configured);
        Assert.Equal(2, await fresh.GetSessionVersionAsync());
        Assert.Equal(2, await fresh.VerifyAsync("a-brand-new-password"));
        Assert.Null(await fresh.VerifyAsync(Configured));

        AdminAccount stored = await h.ReadAccountAsync();
        Assert.Equal(h.Time.GetUtcNow(), stored.PasswordChangedAt);
        Assert.True(stored.PasswordChangedAt > stored.CreatedAt);
    }

    [Fact]
    public async Task ResetGivesANewPasswordAndKicksEverySession()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();

        string password = await h.Service.ResetAsync();

        Assert.Equal(20, password.Length);
        AdminAccountService fresh = h.NewService(Configured);
        Assert.Equal(2, await fresh.VerifyAsync(password));
        Assert.Null(await fresh.VerifyAsync(Configured));
    }

    [Fact]
    public async Task ResetCreatesTheAccountWhenThereIsNone()
    {
        await using Harness h = await Harness.StartAsync(password: null);

        string password = await h.Service.ResetAsync();

        Assert.Equal(1, await h.NewService(null).VerifyAsync(password));
    }

    [Fact]
    public async Task OnlyTheHashIsStored()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();

        AdminAccount stored = await h.ReadAccountAsync();

        Assert.DoesNotContain(Configured, stored.PasswordHash, StringComparison.Ordinal);

        // Identity v3 格式：第一个字节是 0x01，所以 base64 以 AQAAAA 开头。
        Assert.StartsWith("AQAAAA", stored.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAccountMeansNoSessionAndNothingVerifies()
    {
        await using Harness h = await Harness.StartAsync(password: null);

        Assert.Null(await h.Service.GetSessionVersionAsync());
        Assert.Null(await h.Service.VerifyAsync("anything-at-all"));
    }

    [Fact]
    public void GeneratedPasswordsDiffer()
    {
        Assert.NotEqual(AdminAccountService.GeneratePassword(), AdminAccountService.GeneratePassword());
    }

    [Fact]
    public async Task OldPbkdf2HashIsUpgradedOnlyAfterSuccessfulLogin()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();
        AdminAccount before = await h.ReadAccountAsync();
        var oldHasher = new PasswordHasher<AdminAccount>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));
        string oldHash = oldHasher.HashPassword(before, Configured);
        await h.ReplaceHashAsync(oldHash);

        AdminAccountService fresh = h.NewService(null);
        Assert.Null(await fresh.VerifyAsync("a-wrong-password"));
        Assert.Equal(oldHash, (await h.ReadAccountAsync()).PasswordHash);
        Assert.Equal(1, await fresh.VerifyAsync(Configured));

        AdminAccount upgraded = await h.ReadAccountAsync();
        byte[] hash = Convert.FromBase64String(upgraded.PasswordHash);
        Assert.Equal(1, hash[0]); // Identity V3
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1, 4))); // HMAC-SHA512
        Assert.Equal(220_000u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5, 4)));
        Assert.NotEqual(oldHash, upgraded.PasswordHash);
        Assert.Equal(before.SessionVersion, upgraded.SessionVersion);
        Assert.Equal(before.PasswordChangedAt, upgraded.PasswordChangedAt);
        Assert.False(upgraded.MustChangePassword);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedAndResetPasswordsMustBeReplacedWithADifferentPassword(bool reset)
    {
        await using Harness h = await Harness.StartAsync(password: null);
        string generated = reset
            ? await h.Service.ResetAsync()
            : (await h.Service.EnsureCreatedAsync())!;
        AdminAccountService fresh = h.NewService(Configured);
        Assert.Null(await fresh.EnsureCreatedAsync());
        Assert.Equal(new AdminSessionState(1, true), await fresh.GetSessionStateAsync());

        PasswordChangeResult same = await fresh.ChangePasswordAsync(generated, generated);
        Assert.Equal(PasswordChangeStatus.UnchangedGeneratedPassword, same.Status);
        Assert.Equal(1, same.SessionVersion);
        Assert.True((await h.ReadAccountAsync()).MustChangePassword);
        Assert.Equal(1, await fresh.VerifyAsync(generated));

        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword,
            (await fresh.ChangePasswordAsync("a-wrong-password", Configured)).Status);
        Assert.Equal(PasswordChangeStatus.TooShort,
            (await fresh.ChangePasswordAsync(generated, "short")).Status);
        Assert.True((await h.ReadAccountAsync()).MustChangePassword);

        Assert.Equal(PasswordChangeStatus.Changed, (await fresh.ChangePasswordAsync(generated, Configured)).Status);
        AdminAccountService restarted = h.NewService(null);
        Assert.Equal(new AdminSessionState(2, false), await restarted.GetSessionStateAsync());
        Assert.Null(await restarted.VerifyAsync(generated));
        Assert.Equal(2, await restarted.VerifyAsync(Configured));
    }

    [Fact]
    public async Task ResetAnExistingAccountRequiresChangingItsPasswordAgain()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync();
        Assert.False((await h.ReadAccountAsync()).MustChangePassword);
        string generated = await h.Service.ResetAsync();
        Assert.Equal(new AdminSessionState(2, true), await h.NewService(null).GetSessionStateAsync());
        Assert.Equal(PasswordChangeStatus.Changed, (await h.Service.ChangePasswordAsync(generated, Configured)).Status);
        Assert.Equal(new AdminSessionState(3, false), await h.NewService(null).GetSessionStateAsync());
    }

    [Fact]
    public async Task APreviouslyUsedContextCannotVerifyOrOverwriteAResetPassword()
    {
        await using Harness h = await Harness.StartAsync(Configured);
        await h.Service.EnsureCreatedAsync(); // This context tracks the original entity.
        Assert.Equal(1, await h.Service.VerifyAsync(Configured));
        AdminAccountService other = h.NewService(null);
        string reset = await other.ResetAsync();

        Assert.Null(await h.Service.VerifyAsync(Configured));
        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword,
            (await h.Service.ChangePasswordAsync(Configured, "attacker-replacement")).Status);
        Assert.Equal(2, await h.Service.VerifyAsync(reset));
        await h.Service.RevokeSessionsAsync();
        Assert.Equal(new AdminSessionState(3, true), await other.GetSessionStateAsync());
        await other.RevokeSessionsAsync();
        Assert.Equal(new AdminSessionState(4, true), await h.Service.GetSessionStateAsync());
    }

    [Fact]
    public async Task SeparateConnectionsCannotLoseSessionVersionIncrementsOrCreateTwoAccounts()
    {
        string file = Path.Combine(Path.GetTempPath(), "ntm-auth-race-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 10 }.ToString())
            .Options;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [AdminAccountService.InitialPasswordVariable] = Configured }).Build();
        try
        {
            await using (var migrate = new ConfigDbContext(options))
            {
                await migrate.Database.MigrateAsync();
            }

            // Independent connections represent requests or a concurrently invoked CLI reset.
            async Task RunConcurrentAsync(Func<AdminAccountService, Task> action)
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task[] workers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
                {
                    await using var db = new ConfigDbContext(options);
                    var service = new AdminAccountService(db, configuration, TimeProvider.System);
                    await start.Task;
                    await action(service);
                })).ToArray();
                start.SetResult();
                await Task.WhenAll(workers);
            }

            await RunConcurrentAsync(async service => Assert.Null(await service.EnsureCreatedAsync()));
            await RunConcurrentAsync(service => service.RevokeSessionsAsync());
            await using (var read = new ConfigDbContext(options))
            {
                Assert.Equal(7, (await read.AdminAccounts.SingleAsync()).SessionVersion);
            }

            await RunConcurrentAsync(async service => _ = await service.ResetAsync());
            await using var final = new ConfigDbContext(options);
            AdminAccount account = await final.AdminAccounts.SingleAsync();
            Assert.Equal(13, account.SessionVersion);
            Assert.True(account.MustChangePassword);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Path.GetDirectoryName(Path.GetFullPath(file)) + Path.DirectorySeparatorChar);
            Assert.StartsWith("ntm-auth-race-", Path.GetFileName(file), StringComparison.Ordinal);
            File.Delete(file);
        }
    }

    /// <summary>内存 SQLite，跑真实迁移；同一个连接上可以开新的 DbContext 模拟下一个请求。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly List<ConfigDbContext> _contexts = [];

        private Harness(SqliteConnection connection)
        {
            _connection = connection;
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 26, 4, 0, 0, TimeSpan.Zero));

        public AdminAccountService Service { get; private set; } = null!;

        public static async Task<Harness> StartAsync(string? password)
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();
            var harness = new Harness(connection);
            await harness.NewContext().Database.MigrateAsync();
            harness.Service = harness.NewService(password);
            return harness;
        }

        public AdminAccountService NewService(string? password)
        {
            var values = new Dictionary<string, string?>();
            if (password is not null)
            {
                values[AdminAccountService.InitialPasswordVariable] = password;
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return new AdminAccountService(NewContext(), configuration, Time);
        }

        public async Task ReplaceHashAsync(string hash)
        {
            ConfigDbContext db = NewContext();
            AdminAccount account = await db.AdminAccounts.SingleAsync();
            account.PasswordHash = hash;
            await db.SaveChangesAsync();
        }

        public async Task<AdminAccount> ReadAccountAsync() =>
            await NewContext().AdminAccounts.AsNoTracking().SingleAsync();

        public async ValueTask DisposeAsync()
        {
            foreach (ConfigDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private ConfigDbContext NewContext()
        {
            DbContextOptions<ConfigDbContext> options = new DbContextOptionsBuilder<ConfigDbContext>()
                .UseSqlite(_connection)
                .Options;
            var context = new ConfigDbContext(options);
            _contexts.Add(context);
            return context;
        }
    }
}
