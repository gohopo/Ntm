using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Security;

public enum PasswordChangeStatus
{
    Changed = 1,
    WrongCurrentPassword = 2,
    TooShort = 3,
}

/// <param name="Status">结果。</param>
/// <param name="SessionVersion">改成功后的新会话版本；当前会话要用它重新签发 Cookie。</param>
public readonly record struct PasswordChangeResult(PasswordChangeStatus Status, int SessionVersion);

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

    private static readonly PasswordHasher<AdminAccount> Hasher = new();

    /// <summary>
    /// 库里没有管理员就建一个：有 <see cref="InitialPasswordVariable"/> 用它，没有就生成随机密码。
    /// 返回生成的密码（调用方负责只打印这一次）；用了环境变量或账号已存在时返回 null。
    /// </summary>
    /// <exception cref="InvalidOperationException">环境变量给的密码太短。宁可启动失败，也不悄悄换成别的密码。</exception>
    public async Task<string?> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
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
        };
        account.PasswordHash = Hasher.HashPassword(account, password);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return generated;
    }

    /// <summary>校验密码。对了返回当前会话版本，错了返回 null。哈希算法升级过就顺手重哈希。</summary>
    public async Task<int?> VerifyAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

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
            account.PasswordHash = Hasher.HashPassword(account, password);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return account.SessionVersion;
    }

    /// <summary>当前会话版本。每个带 Cookie 的管理请求都要比一次，所以只读这一列、不跟踪实体。</summary>
    public Task<int?> GetSessionVersionAsync(CancellationToken cancellationToken = default) =>
        db.AdminAccounts
            .AsNoTracking()
            .Where(a => a.Id == AdminAccount.SingletonId)
            .Select(a => (int?)a.SessionVersion)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PasswordChangeResult> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

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

        SetPassword(account, newPassword);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PasswordChangeResult(PasswordChangeStatus.Changed, account.SessionVersion);
    }

    /// <summary>
    /// 命令行重置：生成新的随机密码并返回，所有会话失效。账号不存在就建一个。
    /// 不接受从参数传入密码，免得它留在 shell 历史里。
    /// </summary>
    public async Task<string> ResetAsync(CancellationToken cancellationToken = default)
    {
        string password = GeneratePassword();
        AdminAccount? account = await FindAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            account = new AdminAccount { PasswordHash = string.Empty, CreatedAt = now, PasswordChangedAt = now };
            account.PasswordHash = Hasher.HashPassword(account, password);
            db.AdminAccounts.Add(account);
        }
        else
        {
            SetPassword(account, password);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return password;
    }

    /// <summary>约 115 位熵的随机密码。</summary>
    public static string GeneratePassword() =>
        RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);

    private Task<AdminAccount?> FindAsync(CancellationToken cancellationToken) =>
        db.AdminAccounts.FirstOrDefaultAsync(a => a.Id == AdminAccount.SingletonId, cancellationToken);

    private void SetPassword(AdminAccount account, string password)
    {
        account.PasswordHash = Hasher.HashPassword(account, password);
        account.SessionVersion++;
        account.PasswordChangedAt = timeProvider.GetUtcNow();
    }
}
