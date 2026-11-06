namespace Ntm.Gateway.Traffic;

/// <summary>
/// 按小时 × 渠道 × 下发模型 × 上游模型 × API 密钥汇总。和请求记录同库，并且在同一个事务里随记录批次一起写（ADR-0004）：
/// WAL 下跨文件事务只按单个文件原子，分两个库崩溃时就会对不上。
/// 记录过了保留期会删，这张表不删——它是账（Q42/Q43）。
/// </summary>
public sealed class HourlyAggregate
{
    public long Id { get; set; }

    /// <summary>这一小时的起点，UTC，分秒为零。</summary>
    public DateTimeOffset HourUtc { get; set; }

    public int ChannelId { get; set; }

    /// <summary>反范式，渠道在 ntm.db。渠道改名或删掉之后，老行里还是当时的名字（Q60）。</summary>
    public required string ChannelName { get; set; }

    /// <summary>下发模型名（Q44）。按名字分组：客户端就是按这个名字调的。</summary>
    public required string PublishedModel { get; set; }

    public required string UpstreamModel { get; set; }

    /// <summary>API 密钥 ID。0 表示没有 API 密钥，即 Workbench 发的请求（Q208）。不用 null，否则唯一索引里每行都算不同。</summary>
    public int ApiKeyId { get; set; }

    /// <summary>反范式的 API 密钥名（Q60）。API 密钥删了之后统计里还能显示它当时的名字。</summary>
    public string? ApiKeyName { get; set; }

    public long RequestCount { get; set; }

    public long SuccessCount { get; set; }

    public long FailureCount { get; set; }

    /// <summary>客户端断开 + 手动中断的请求数（Q44）。它们既不算成功也不算失败。</summary>
    public long CanceledCount { get; set; }

    /// <summary>算不出费用的请求数：用了 Token 但缺单价（Q44）。</summary>
    public long UnpricedCount { get; set; }

    /// <summary>用量是网关估算的请求数（Q44）。</summary>
    public long EstimatedCount { get; set; }

    /// <summary>总 Attempt 数。除以 RequestCount 就是平均重试次数。</summary>
    public long AttemptCount { get; set; }

    /// <summary>
    /// 因上游原因失败的 Attempt 数（Q57）：不含客户端自己的问题（请求有误、断开、手动中断）。
    /// 除以 AttemptCount 就是这个渠道的尝试失败率——老是失败但被换掉的渠道，
    /// 只看请求成功率是看不出来的。
    /// </summary>
    public long FailedAttemptCount { get; set; }

    public long InputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWrite5mTokens { get; set; }

    public long CacheWrite1hTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>纳美元合计（ADR-0009），已乘倍率。缺单价的那些请求不计入，用 UnpricedCount 单独记。</summary>
    public long CostNanoUsd { get; set; }

    /// <summary>
    /// 首字时间的分桶计数（Q45）：十个桶，&lt;0.25s、&lt;0.5s、&lt;1s、&lt;2s、&lt;4s、&lt;8s、&lt;16s、&lt;32s、&lt;64s、≥64s。
    /// 只有流式请求进桶（Q58）。不存原始样本，P50 / P95 从桶里估。
    /// </summary>
    public long TimeToFirstContentUnder250ms { get; set; }

    public long TimeToFirstContentUnder500ms { get; set; }

    public long TimeToFirstContentUnder1s { get; set; }

    public long TimeToFirstContentUnder2s { get; set; }

    public long TimeToFirstContentUnder4s { get; set; }

    public long TimeToFirstContentUnder8s { get; set; }

    public long TimeToFirstContentUnder16s { get; set; }

    public long TimeToFirstContentUnder32s { get; set; }

    public long TimeToFirstContentUnder64s { get; set; }

    public long TimeToFirstContentOver64s { get; set; }

    /// <summary>首字时间之和，毫秒。只用来算平均值。</summary>
    public long TimeToFirstContentMillisecondsSum { get; set; }

    public long TimeToFirstContentSampleCount { get; set; }

    /// <summary>把一个首字时间记进对应的桶。</summary>
    public void AddTimeToFirstContent(long milliseconds)
    {
        TimeToFirstContentMillisecondsSum += milliseconds;
        TimeToFirstContentSampleCount++;

        switch (milliseconds)
        {
            case < 250:
                TimeToFirstContentUnder250ms++;
                break;
            case < 500:
                TimeToFirstContentUnder500ms++;
                break;
            case < 1_000:
                TimeToFirstContentUnder1s++;
                break;
            case < 2_000:
                TimeToFirstContentUnder2s++;
                break;
            case < 4_000:
                TimeToFirstContentUnder4s++;
                break;
            case < 8_000:
                TimeToFirstContentUnder8s++;
                break;
            case < 16_000:
                TimeToFirstContentUnder16s++;
                break;
            case < 32_000:
                TimeToFirstContentUnder32s++;
                break;
            case < 64_000:
                TimeToFirstContentUnder64s++;
                break;
            default:
                TimeToFirstContentOver64s++;
                break;
        }
    }

    /// <summary>十个桶的计数，从 &lt;0.25s 到 ≥64s。</summary>
    public long[] TimeToFirstContentBuckets() =>
    [
        TimeToFirstContentUnder250ms,
        TimeToFirstContentUnder500ms,
        TimeToFirstContentUnder1s,
        TimeToFirstContentUnder2s,
        TimeToFirstContentUnder4s,
        TimeToFirstContentUnder8s,
        TimeToFirstContentUnder16s,
        TimeToFirstContentUnder32s,
        TimeToFirstContentUnder64s,
        TimeToFirstContentOver64s,
    ];

    /// <summary>把另一行的计数加进这一行。用于按维度合并。</summary>
    public void Add(HourlyAggregate other)
    {
        ArgumentNullException.ThrowIfNull(other);

        RequestCount += other.RequestCount;
        SuccessCount += other.SuccessCount;
        FailureCount += other.FailureCount;
        CanceledCount += other.CanceledCount;
        UnpricedCount += other.UnpricedCount;
        EstimatedCount += other.EstimatedCount;
        AttemptCount += other.AttemptCount;
        FailedAttemptCount += other.FailedAttemptCount;
        InputTokens += other.InputTokens;
        CacheReadTokens += other.CacheReadTokens;
        CacheWrite5mTokens += other.CacheWrite5mTokens;
        CacheWrite1hTokens += other.CacheWrite1hTokens;
        OutputTokens += other.OutputTokens;
        CostNanoUsd += other.CostNanoUsd;
        TimeToFirstContentUnder250ms += other.TimeToFirstContentUnder250ms;
        TimeToFirstContentUnder500ms += other.TimeToFirstContentUnder500ms;
        TimeToFirstContentUnder1s += other.TimeToFirstContentUnder1s;
        TimeToFirstContentUnder2s += other.TimeToFirstContentUnder2s;
        TimeToFirstContentUnder4s += other.TimeToFirstContentUnder4s;
        TimeToFirstContentUnder8s += other.TimeToFirstContentUnder8s;
        TimeToFirstContentUnder16s += other.TimeToFirstContentUnder16s;
        TimeToFirstContentUnder32s += other.TimeToFirstContentUnder32s;
        TimeToFirstContentUnder64s += other.TimeToFirstContentUnder64s;
        TimeToFirstContentOver64s += other.TimeToFirstContentOver64s;
        TimeToFirstContentMillisecondsSum += other.TimeToFirstContentMillisecondsSum;
        TimeToFirstContentSampleCount += other.TimeToFirstContentSampleCount;
    }
}
