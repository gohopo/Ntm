namespace Ntm.Gateway.Traffic;

/// <summary>
/// 冷却的作用域，四者之一（Q28）。失效的那一层退出选路，同渠道其余部分照常服务。
/// 「所有密钥或所有模型都在冷却」不单独存状态，选路时现算。
/// </summary>
public enum CooldownScope
{
    None = 0,

    /// <summary>按密钥值归集，不按行 ID：同一份密钥配到多个 Channel 也一起冷却（Q21）。</summary>
    Key = 1,

    /// <summary>这把密钥上的这个上游模型。上游多半是按账号×模型限流的。</summary>
    KeyModel = 2,

    /// <summary>这个渠道上的这个上游模型。</summary>
    ChannelModel = 3,

    /// <summary>整个渠道。</summary>
    Channel = 4,
}
