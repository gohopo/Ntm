namespace Ntm.Gateway.Audit;

/// <summary>
/// 操作记录（Q358）：管理员做过什么。和「请求记录」（<see cref="Traffic.RequestRecord"/>）分开——
/// 请求记录是 API 客户端经网关发出的请求，操作记录是管理员在管理接口和命令行上的改动。
/// 只记改了哪些字段，不记字段的值；密钥、密码、代理地址这类只写「已更换」。
/// </summary>
public sealed class AuditEntry
{
    public const int MaxActionLength = 200;

    public const int MaxNameLength = 200;

    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>来源地址。命令行上的操作为 null。</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>
    /// 动作。登录这类有固定的名字（<see cref="AuditActions"/>）；其余管理接口是「方法 路由模板」，
    /// 例如 <c>PUT /api/channels/{id}</c>。
    /// </summary>
    public required string Action { get; set; }

    /// <summary>登录失败记 false，其余都是成功了才记。</summary>
    public bool Succeeded { get; set; } = true;

    /// <summary>对象类型，取自路由的第一段，例如 <c>channels</c>、<c>api-keys</c>。</summary>
    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>对象当时的名字，删掉之后也能看出删的是哪个。</summary>
    public string? TargetName { get; set; }

    /// <summary>这次改动落到库里的每一行。</summary>
    public List<AuditChange> Changes { get; set; } = [];
}

/// <summary>一行配置的变化。</summary>
public sealed class AuditChange
{
    /// <summary>实体类型名，例如 <c>Channel</c>、<c>ChannelKey</c>。</summary>
    public required string Entity { get; set; }

    public string? EntityId { get; set; }

    public string? Name { get; set; }

    /// <summary><c>added</c>、<c>modified</c> 或 <c>deleted</c>。</summary>
    public required string State { get; set; }

    /// <summary>
    /// 改了的字段名，逗号分隔，只在 <c>modified</c> 时有。敏感字段合成一个名字（<c>Secret</c>、<c>Proxy</c>、<c>Password</c>），
    /// 页面上显示成「已更换」。
    /// </summary>
    public string? Fields { get; set; }
}

/// <summary>固定名字的动作。</summary>
public static class AuditActions
{
    public const string Login = "auth.login";

    public const string Logout = "auth.logout";

    public const string LogoutAll = "auth.logout-all";

    public const string PasswordChanged = "auth.password";

    public const string PasswordReset = "cli.reset-admin-password";
}
