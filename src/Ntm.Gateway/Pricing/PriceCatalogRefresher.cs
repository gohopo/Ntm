using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ntm.Gateway.Pricing;

/// <summary>
/// 后台刷新价目目录（Q69）：启动先读缓存，然后刷一次，之后每 RefreshIntervalHours 小时再刷；
/// 失败后一小时重试。间隔设 0 就只读缓存，不自动下载，留给单价页的「立即刷新」。
///
/// 渠道新声明了一个缓存里没有的供应商时，<see cref="Wake"/> 会把等待提前结束，
/// 这样新渠道不用等一整个周期才有单价。
/// </summary>
public sealed class PriceCatalogRefresher(
    PriceCatalogService catalog,
    IOptions<PricingOptions> options) : BackgroundService
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>有新的供应商要取单价时叫醒刷新循环。</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经排了一次唤醒，不用再排。
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await catalog.LoadCacheAsync(stoppingToken).ConfigureAwait(false);

        TimeSpan interval = options.Value.RefreshInterval;
        if (interval <= TimeSpan.Zero)
        {
            // 自动刷新关掉了（间隔 0）：只用缓存，任何下载都等单价页的「立即刷新」。
            return;
        }

        // 第一轮按周期处理：先刷一次。
        bool scheduled = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool refreshed = true;

            // 被叫醒的那一轮只在真的缺供应商时才下载，免得一次改渠道就白拉一份 5.9 MB。
            if (scheduled || await catalog.NeedsProvidersAsync(stoppingToken).ConfigureAwait(false))
            {
                refreshed = await catalog.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }

            TimeSpan wait = refreshed ? interval : Min(RetryAfterFailure, interval);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(wait);
            try
            {
                await _wake.WaitAsync(timeout.Token).ConfigureAwait(false);
                scheduled = false;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // 等够了，按周期刷。
                scheduled = true;
            }
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
