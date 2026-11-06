namespace Ntm.Gateway.Traffic;

/// <summary>一条 Request 的终态。</summary>
public enum RequestStatus
{
    /// <summary>还在进行中。</summary>
    Running = 0,

    /// <summary>客户端拿到了完整响应。</summary>
    Succeeded = 1,

    /// <summary>所有候选都失败了，或者提交后上游断链。</summary>
    Failed = 2,

    /// <summary>客户端自己断开。用量按观测到的估算（Q20）。</summary>
    Canceled = 3,

    /// <summary>在日志页手动中断（Q15）。不触发故障切换，不计入渠道健康。</summary>
    Aborted = 4,
}
