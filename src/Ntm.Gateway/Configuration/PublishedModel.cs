using Ntm.Protocol;

namespace Ntm.Gateway.Configuration;

/// <summary>
/// 网关对客户端下发的一个模型名。它是独立实体，不挂在任何 Channel 下面；
/// 和 UpstreamModel 的关系由 MatchRule 表达。<c>/v1/models</c> 返回的就是它的清单。
/// 新建默认不启用：下发什么模型是人的决定，系统不替人做。
/// </summary>
public sealed class PublishedModel
{
    public int Id { get; set; }

    /// <summary>客户端请求里写的那个名字。全局唯一，大小写敏感。</summary>
    public required string Name { get; set; }

    /// <summary>界面上显示的名字。为空时显示 <see cref="Name"/>。</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 这个模型服务哪种请求。选路时按它过滤候选——嵌入请求不能送给只会对话的 Channel（Q13、Q35）。
    /// </summary>
    public RequestKind RequestKind { get; set; } = RequestKind.Chat;

    /// <summary>默认 false。模型同步永远不会把它改成 true（Q39）。</summary>
    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<MatchRule> Rules { get; } = [];
}
