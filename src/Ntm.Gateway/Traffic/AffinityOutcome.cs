namespace Ntm.Gateway.Traffic;

/// <summary>这个 Request 和 Affinity 的关系（Q122）。没有算出 Conversation（没有消息）时整个字段为 null。</summary>
public enum AffinityOutcome
{
    /// <summary>这个 Conversation 第一次出现（或上次的记忆已经过期），按平滑加权轮询选的。</summary>
    New = 0,

    /// <summary>落在了上次记住的渠道和渠道密钥上。</summary>
    Kept = 1,

    /// <summary>记住的那个不可用、或已不在当前最高可用档，换到了别处。</summary>
    Moved = 2,
}
