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
