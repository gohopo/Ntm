using Ntm.Gateway.Restrictions;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// 选路时被挡掉的一个候选（Q263）：请求没走主力渠道时，事后能看出为什么。存成请求记录上的 JSON 列，
/// 只在有候选被挡时才有内容，最多 <see cref="RequestRecord.MaxSkippedCandidates"/> 条。
/// </summary>
public sealed class SkippedCandidate
{
    public int ChannelId { get; set; }

    public required string ChannelName { get; set; }

    public required string UpstreamModel { get; set; }

    public BlockReason Reason { get; set; }

    /// <summary>预计什么时候恢复；说不准时为 null。存 Unix 毫秒，JSON 列里不走时间转换器。</summary>
    public long? RecoversAtMs { get; set; }
}
