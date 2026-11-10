using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Security;

public enum PasswordChangeStatus
{
    Changed = 1,
    WrongCurrentPassword = 2,
    TooShort = 3,
    UnchangedGeneratedPassword = 4,
}

/// <param name="Status">结果。</param>
/// <param name="SessionVersion">改成功后的新会话版本；当前会话要用它重新签发 Cookie。</param>
public readonly record struct PasswordChangeResult(PasswordChangeStatus Status, int SessionVersion);

/// <param name="SessionVersion">当前会话版本。</param>
/// <param name="MustChangePassword">还在用网关生成的密码，必须先改（Q349）。</param>
public readonly record struct AdminSessionState(int SessionVersion, bool MustChangePassword);

/// <summary>
/// 管理员账号：首次启动建号、登录校验、改密码、命令行重置（Q33）。
/// 密码只以哈希落库；改密码与重置都会让会话版本加一，旧会话一律失效。
/// </summary>
public sealed class AdminAccountService(ConfigDbContext db, IConfiguration configuration, TimeProvider timeProvider)
{
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// 首次启动时读取的初始密码。只在库里还没有管理员时读，之后改它不会改库——
    /// 否则「容器重启一次密码就被环境变量改回去」会是一个很难查的坑。
    /// </summary>
    public const string InitialPasswordVariable = "NTM_ADMIN_PASSWORD";

    /// <summary>随机密码的字母表：去掉了 0/O、1/l/I 这类容易抄错的字符。</summary>
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int GeneratedPasswordLength = 20;

    /// <summary>PBKDF2-HMAC-SHA512 的迭代次数，按 OWASP 的建议取 22 万（Q355）。旧哈希在下次登录成功时重算。</summary>
    public const int PasswordHashIterations = 220_000;

    private static readonly PasswordHasher<AdminAccount> Hasher = new(
        Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = PasswordHashIterations }));

    /// <summary>
    /// 库里没有管理员就建一个：有 <see cref="InitialPasswordVariable"/> 用它，没有就生成随机密码。
    /// 返回生成的密码（调用方负责只打印这一次）；用了环境变量或账号已存在时返回 null。
    /// </summary>
    /// <exception cref="InvalidOperationException">环境变量给的密码太短。宁可启动失败，也不悄悄换成别的密码。</exception>
    public async Task<string?> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        // SQLite 的非延迟事务先取得写锁，多个进程同时首次启动也只能创建一次。
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await db.AdminAccounts.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? configured = configuration[InitialPasswordVariable];
        string? generated = null;
        string password;
        if (string.IsNullOrEmpty(configured))
        {
            generated = GeneratePassword();
            password = generated;
        }
        else if (configured.Length < MinimumPasswordLength)
        {
            throw new InvalidOperationException(
                $"{InitialPasswordVariable} 至少要 {MinimumPasswordLength} 个字符。");
        }
        else
        {
            password = configured;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var account = new AdminAccount
        {
            PasswordHash = string.Empty,
            CreatedAt = now,
            PasswordChangedAt = now,

            // 生成的密码打印在启动日志里，谁能看日志谁就知道它（Q349）。
            MustChangePassword = generated is not null,
        };
        account.PasswordHash = Hasher.HashPassword(account, password);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generated;
    }

    /// <summary>校验密码。对了返回当前会话版本，错了返回 null。哈希算法升级过就顺手重哈希。</summary>
    public async Task<int?> VerifyAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        // 验证与可能的重哈希是一件事：不能用旧密码验证后覆盖另一个进程刚重置的哈希。
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AdminAccount? account = await FindAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        PasswordVerificationResult result = Hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            string hash = Hasher.HashPassword(account, password);
            await db.AdminAccounts.Where(a => a.Id == AdminAccount.SingletonId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.PasswordHash, hash), cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account.SessionVersion;
    }

    /// <summary>当前会话版本。每个带 Cookie 的管理请求都要比一次，所以只读这一列、不跟踪实体。</summary>
    public async Task<int?> GetSessionVersionAsync(CancellationToken cancellationToken = default) =>
        (await GetSessionStateAsync(cancellationToken).ConfigureAwait(false))?.SessionVersion;

    /// <summary>会话版本和「必须改密码」标志，只读这两列、不跟踪实体。</summary>
    public async Task<AdminSessionState?> GetSessionStateAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.AdminAccounts
            .AsNoTracking()
            .Where(a => a.Id == AdminAccount.SingletonId)
            .Select(a => new { a.SessionVersion, a.MustChangePassword })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : new AdminSessionState(row.SessionVersion, row.MustChangePassword);
    }

    /// <summary>「退出所有设备」（Q354）：会话版本加一，包括当前会话在内的所有会话失效。密码不变。</summary>
    public async Task RevokeSessionsAsync(CancellationToken cancellationToken = default)
    {
        // 在库里原子递增，不把两个同时读到 v1 的退出操作都写成 v2。
        await db.AdminAccounts.Where(a => a.Id == AdminAccount.SingletonId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.SessionVersion, a => a.SessionVersion + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // 与 CLI 重置、其他会话改密码互斥；事务外验证旧密码会产生“重置后被旧请求改回去”的窗口。
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // 先验旧密码再看新密码：没有旧密码的人连「新密码太短」这种信息也拿不到。
        AdminAccount? account = await FindAsync(cancellationToken).ConfigureAwait(false);
        if (account is null
            || Hasher.VerifyHashedPassword(account, account.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            return new PasswordChangeResult(PasswordChangeStatus.WrongCurrentPassword, 0);
        }

        if (newPassword.Length < MinimumPasswordLength)
        {
            return new PasswordChangeResult(PasswordChangeStatus.TooShort, account.SessionVersion);
        }

        if (account.MustChangePassword && string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return new PasswordChangeResult(PasswordChangeStatus.UnchangedGeneratedPassword, account.SessionVersion);
        }

        await ReplacePasswordAsync(account, newPassword, mustChange: false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PasswordChangeResult(PasswordChangeStatus.Changed, account.SessionVersion + 1);
    }

    /// <summary>
    /// 命令行重置：生成新的随机密码并返回，所有会话失效，并标成必须改密码。账号不存在就建一个。
    /// 不接受从参数传入密码，免得它留在 shell 历史里。
    /// </summary>
    public async Task<string> ResetAsync(CancellationToken cancellationToken = default)
    {
        string password = GeneratePassword();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AdminAccount? account = await FindAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            account = new AdminAccount { PasswordHash = string.Empty, CreatedAt = now, PasswordChangedAt = now, MustChangePassword = true };
            account.PasswordHash = Hasher.HashPassword(account, password);
            db.AdminAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 新密码印在终端里，可能留在终端记录或 CI 日志中，登录后先改掉（Q349）。
            await ReplacePasswordAsync(account, password, mustChange: true, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return password;
    }

    /// <summary>约 115 位熵的随机密码。</summary>
    public static string GeneratePassword() =>
        RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);

    private Task<AdminAccount?> FindAsync(CancellationToken cancellationToken) =>
        // 同一服务可能在别的 DbContext 改密码前读过账号；不能复用跟踪器中的旧哈希和版本。
        db.AdminAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AdminAccount.SingletonId, cancellationToken);

    private Task<int> ReplacePasswordAsync(AdminAccount account, string password, bool mustChange, CancellationToken cancellationToken)
    {
        string hash = Hasher.HashPassword(account, password);
        DateTimeOffset changedAt = timeProvider.GetUtcNow();
        return db.AdminAccounts.Where(a => a.Id == AdminAccount.SingletonId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.PasswordHash, hash)
                .SetProperty(a => a.SessionVersion, a => a.SessionVersion + 1)
                .SetProperty(a => a.PasswordChangedAt, changedAt)
                .SetProperty(a => a.MustChangePassword, mustChange), cancellationToken);
    }
}
