namespace Ntm.Gateway.Pricing;

/// <summary>价目目录的配置（Q69/Q72）。</summary>
public sealed class PricingOptions
{
    public const string SectionName = "Ntm:Pricing";

    /// <summary>目录地址。</summary>
    public string CatalogUrl { get; set; } = "https://models.dev/api.json";

    /// <summary>刷新间隔小时数。默认 24，设 0 关闭自动刷新（启动时也不刷），只留手动「立即刷新」。</summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>缓存文件名，放在数据目录下。</summary>
    public string CacheFileName { get; set; } = "price-catalog.json";

    /// <summary>一次下载的超时秒数。整份 5.9 MB，gzip 后约 605 KB。</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    public TimeSpan RefreshInterval => TimeSpan.FromHours(RefreshIntervalHours);

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);
}
