namespace Ntm.Gateway.Debugging;

/// <summary>DebugCapture 的运行参数（Q154/Q157/Q159/Q160）。</summary>
public sealed class DebugCaptureOptions
{
    public const string SectionName = "Ntm:Debug";

    /// <summary>原文保留多久，默认 24 小时（Q159）。</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>开一次最长多久，默认 1 小时；界面上不允许超过 <see cref="MaxDurationHours"/>（Q154）。</summary>
    public int DefaultDurationMinutes { get; set; } = 60;

    /// <summary>一次最多能开多久，默认 24 小时（Q154）。</summary>
    public int MaxDurationHours { get; set; } = 24;

    /// <summary>落盘队列容量。满了就丢新的并记数，绝不拖慢转发（Q160）。</summary>
    public int QueueCapacity { get; set; } = 256;

    /// <summary>一次写盘最多攒多少份。</summary>
    public int WriteBatchSize { get; set; } = 32;

    /// <summary>清理间隔。</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);
}
