namespace Ntm.Gateway.Traffic;

/// <summary>请求记录的保留期（Q42）。环境变量 <c>Ntm__Traffic__RetentionDays</c> 可以覆盖。</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Ntm:Traffic";

    /// <summary>
    /// 请求记录（Request + Attempt）保留多少天，默认 7。设 0 或负数表示永久保留。
    /// 汇总（Aggregate）不受影响，永久保留。
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>一个事务里最多删多少条请求。小批次让写入器不会被长时间挡住。</summary>
    public int DeleteBatchSize { get; set; } = 500;

    /// <summary>
    /// ResponseLanding 按最后使用保留多少天，默认 30（Q285）。环境变量
    /// <c>Ntm__Traffic__LandingRetentionDays</c> 可以覆盖；设 0 或负数表示永久保留。
    /// </summary>
    public int LandingRetentionDays { get; set; } = 30;

    /// <summary>
    /// 操作记录保留多少天，默认 180（Q358）。环境变量 <c>Ntm__Traffic__AuditRetentionDays</c> 可以覆盖；
    /// 设 0 或负数表示永久保留。
    /// </summary>
    public int AuditRetentionDays { get; set; } = 180;

    /// <summary>response_landings 最多留这么多行，超了先删最久没用的（Q285）。</summary>
    public int MaxLandings { get; set; } = 1_000_000;
}
