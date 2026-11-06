namespace Ntm.Gateway.Security;

/// <summary>
/// 唯一的管理员（Q7）。表里只有一行，<see cref="Id"/> 恒为 1。
/// 不预留多用户：真要多用户时加一次迁移，比现在维护一个永远是 1 的外键便宜。
/// </summary>
public sealed class AdminAccount
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>ASP.NET Core Identity 的密码哈希格式，自带算法版本，升级时可以无感重哈希。</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// 会话版本。改密码或重置时加一，会话 Cookie 里带的旧版本随之失效，
    /// 所以别处登录着的会话会被踢下线。
    /// </summary>
    public int SessionVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset PasswordChangedAt { get; set; }
}
