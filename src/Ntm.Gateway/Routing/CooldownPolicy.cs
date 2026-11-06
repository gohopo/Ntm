using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 冷却的作用域与时长（Q28、Q29）。作用域由错误类别决定：上游按「账号 × 模型」限流，
/// 所以 429 冷却「密钥 + 模型」——ccLoad 每个 429 都冷却「渠道 + 模型」，一把密钥被限流就连累同渠道所有兄弟密钥，
/// 多密钥的意义就没了。
/// </summary>
public static class CooldownPolicy
{
    /// <summary>自己算的退避最多到这里。上游明确给出的重置时间另算，最多 24 小时。</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>±20% 抖动：几把密钥同时进入冷却时别同时醒来再一起撞上去。</summary>
    public const double JitterRatio = 0.2;

    /// <summary>上次失败已经过去这么久，退避阶梯从头算——很久以前的失败不该让今天的冷却更长。</summary>
    public static readonly TimeSpan LadderResetAfter = TimeSpan.FromHours(1);

    public static CooldownScope ScopeOf(FailureClass failure) => failure switch
    {
        FailureClass.AuthFailure or FailureClass.QuotaExhausted => CooldownScope.Key,
        FailureClass.RateLimited => CooldownScope.KeyModel,
        FailureClass.ModelNotFound
            or FailureClass.UpstreamOverloaded
            or FailureClass.UpstreamServerError
            or FailureClass.FirstEventTimeout
            or FailureClass.IdleTimeout
            or FailureClass.StreamInterrupted
            or FailureClass.SoftError
            or FailureClass.Unknown => CooldownScope.ChannelModel,
        FailureClass.ConnectionFailure => CooldownScope.Channel,

        // 客户端的错、客户端断开、手动中断：不怪上游，不冷却。
        _ => CooldownScope.None,
    };

    public static TimeSpan InitialDelay(FailureClass failure) => failure switch
    {
        FailureClass.AuthFailure => TimeSpan.FromMinutes(5),
        FailureClass.QuotaExhausted or FailureClass.ModelNotFound => TimeSpan.FromMinutes(30),
        FailureClass.FirstEventTimeout
            or FailureClass.IdleTimeout
            or FailureClass.ConnectionFailure => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>
    /// 第 <paramref name="consecutive"/> 次（从 1 起）同类失败的退避：初始值逐次加倍，封顶，再加抖动。
    /// <paramref name="random"/> 返回 [0, 1)，测试里注入固定值。
    /// </summary>
    public static TimeSpan Backoff(FailureClass failure, int consecutive, double random)
    {
        double seconds = InitialDelay(failure).TotalSeconds * Math.Pow(2, Math.Max(0, consecutive - 1));
        seconds = Math.Min(seconds, MaxBackoff.TotalSeconds);

        double jitter = 1 + ((random * 2) - 1) * JitterRatio;
        seconds = Math.Clamp(seconds * jitter, 1, MaxBackoff.TotalSeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}
