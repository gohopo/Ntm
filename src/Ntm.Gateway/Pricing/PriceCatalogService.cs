using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Data;

namespace Ntm.Gateway.Pricing;

/// <summary>价目目录的刷新状态，单价页的页头用它（Q69/Q70）。</summary>
/// <param name="FetchedAt">上次成功取到目录的时间；从没成功过是 null。</param>
/// <param name="LastAttemptAt">上次尝试刷新的时间。</param>
/// <param name="LastError">上次失败的原因；上次成功则为 null。</param>
/// <param name="AutoRefresh">是否开了自动刷新。</param>
/// <param name="Refreshing">现在是否正在刷新。</param>
public sealed record PriceCatalogStatus(
    DateTimeOffset? FetchedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    bool AutoRefresh,
    bool Refreshing);

/// <summary>目录换了一份之后要做的事（自动单价行重新匹配，Q69）。</summary>
public interface IPriceCatalogListener
{
    Task OnCatalogChangedAsync(PriceCatalog current, CancellationToken cancellationToken);
}

/// <summary>
/// 持有当前的价目目录，负责下载、缓存和通知（Q69/Q72）。
///
/// 下载带 gzip 和 If-None-Match；代理走标准的 HTTPS_PROXY 环境变量
/// （和上游转发不同，那边网关自己管代理）。失败时保留原来的目录，只记日志和状态。
/// 成功后只留被渠道声明的那几个供应商的单价，连 ETag 一起写缓存文件。
/// </summary>
public sealed partial class PriceCatalogService : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<PricingOptions> _options;
    private readonly IOptions<NtmDataOptions> _data;
    private readonly TimeProvider _time;
    private readonly ILogger<PriceCatalogService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PriceCatalog _catalog = PriceCatalog.Empty;
    private IReadOnlySet<string> _requested = new HashSet<string>(StringComparer.Ordinal);
    private DateTimeOffset? _lastAttemptAt;
    private string? _lastError;
    private bool _cacheLoaded;
    private int _refreshing;

    public PriceCatalogService(
        IServiceScopeFactory scopes,
        IOptions<PricingOptions> options,
        IOptions<NtmDataOptions> data,
        TimeProvider time,
        ILogger<PriceCatalogService> logger)
        : this(scopes, options, data, time, logger, CreateHandler())
    {
    }

    /// <summary>测试用：注入一个假的 HTTP 处理器。</summary>
    internal PriceCatalogService(
        IServiceScopeFactory scopes,
        IOptions<PricingOptions> options,
        IOptions<NtmDataOptions> data,
        TimeProvider time,
        ILogger<PriceCatalogService> logger,
        HttpMessageHandler handler)
    {
        _scopes = scopes;
        _options = options;
        _data = data;
        _time = time;
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = options.Value.RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ntm/1.0 (+price-catalog)");
    }

    public PriceCatalog Current => Volatile.Read(ref _catalog);

    public PriceCatalogStatus Status => new(
        Current.FetchedAt,
        _lastAttemptAt,
        _lastError,
        _options.Value.RefreshIntervalHours > 0,
        Volatile.Read(ref _refreshing) == 1);

    private string CachePath => Path.Combine(_data.Value.ResolveDirectory(), _options.Value.CacheFileName);

    /// <summary>启动时读缓存（Q72）。读不了就当没有缓存。</summary>
    public async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            _cacheLoaded = true;
            try
            {
                var cached = await PriceCatalogCache.TryReadAsync(CachePath, cancellationToken).ConfigureAwait(false);
                if (cached is { } value)
                {
                    Volatile.Write(ref _catalog, value.Catalog);
                    _requested = value.Requested;
                }
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                LogCacheUnreadable(_logger, CachePath, exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>渠道声明的供应商是不是都已经在缓存里（缺了就要尽快重新下载）。</summary>
    public async Task<bool> NeedsProvidersAsync(CancellationToken cancellationToken)
    {
        IReadOnlySet<string> declared = await DeclaredProvidersAsync(cancellationToken).ConfigureAwait(false);
        return Current.FetchedAt is null || !declared.IsSubsetOf(_requested);
    }

    /// <summary>
    /// 刷新一次。并发调用会排队，不会同时下载两份。
    /// 返回 true 表示目录换了一份或确认没变；false 表示失败（原因在 <see cref="Status"/>）。
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _refreshing, 1);
        try
        {
            _lastAttemptAt = _time.GetUtcNow();
            IReadOnlySet<string> declared = await DeclaredProvidersAsync(cancellationToken).ConfigureAwait(false);
            PriceCatalog current = Current;

            // 缓存里缺了新供应商：丢掉 ETag 重新完整下载，否则 304 拿不到它的单价（Q72）。
            string? etag = declared.IsSubsetOf(_requested) ? current.ETag : null;

            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Value.CatalogUrl);
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            PriceCatalog next;
            if (response.StatusCode == HttpStatusCode.NotModified && etag is not null)
            {
                next = new PriceCatalog(
                    current.Providers,
                    current.PricedProviders.SelectMany(current.PricesOf),
                    current.ETag,
                    _time.GetUtcNow());
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"目录服务返回 HTTP {(int)response.StatusCode}。",
                        null,
                        response.StatusCode);
                }

                long maxBytes = _options.Value.MaxCatalogBytes;
                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new InvalidDataException(SizeLimitedStream.TooLarge(maxBytes));
                }

                await using Stream body = new SizeLimitedStream(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    maxBytes);
                ParsedCatalog parsed = await ModelsDevParser.ParseAsync(body, declared, cancellationToken).ConfigureAwait(false);
                if (parsed.Providers.Count == 0)
                {
                    throw new InvalidDataException("目录是空的，里面一个供应商都没有。");
                }

                next = new PriceCatalog(
                    parsed.Providers,
                    parsed.Prices,
                    response.Headers.ETag?.ToString(),
                    _time.GetUtcNow());
            }

            try
            {
                await PriceCatalogCache.WriteAsync(CachePath, next, declared, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 写不了缓存不影响这次刷新的结果，只是重启后要重新下载。
                LogCacheUnwritable(_logger, CachePath, exception);
            }

            Volatile.Write(ref _catalog, next);
            _requested = declared;
            _lastError = null;
            LogRefreshed(_logger, next.Providers.Count, declared.Count);

            await NotifyAsync(next, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or System.Text.Json.JsonException
            or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // 失败保留原数据，只记日志和状态（Q69）。
            _lastError = Describe(exception);
            LogRefreshFailed(_logger, _lastError, exception);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>所有渠道（包括停用的）声明的供应商。停用的渠道以后还会启用，单价要先备着。</summary>
    private async Task<IReadOnlySet<string>> DeclaredProvidersAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        List<string> providers = await db.Channels
            .AsNoTracking()
            .Where(c => c.Provider != "")
            .Select(c => c.Provider)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return providers.ToHashSet(StringComparer.Ordinal);
    }

    private async Task NotifyAsync(PriceCatalog catalog, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        foreach (IPriceCatalogListener listener in scope.ServiceProvider.GetServices<IPriceCatalogListener>())
        {
            await listener.OnCatalogChangedAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        TaskCanceledException => "下载超时。",
        HttpRequestException { StatusCode: { } status } => $"目录服务返回 HTTP {(int)status}。",
        HttpRequestException http => "连不上目录服务：" + (http.InnerException?.Message ?? http.Message),
        System.Text.Json.JsonException => "目录内容不是合法的 JSON。",
        _ => exception.Message,
    };

    private static SocketsHttpHandler CreateHandler() => new()
    {
        // 5.9 MB 的 JSON，gzip 后约 605 KB（Q69）。
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,

        // 代理用标准环境变量 HTTPS_PROXY / NO_PROXY（Q69）。
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "价目目录已刷新：{ProviderCount} 个供应商，保留了 {KeptCount} 个渠道用到的供应商的单价。")]
    private static partial void LogRefreshed(ILogger logger, int providerCount, int keptCount);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "价目目录刷新失败，继续用原来的数据：{Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "价目目录缓存文件读不了，当作没有缓存：{Path}")]
    private static partial void LogCacheUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "价目目录缓存文件写不了，重启后需要重新下载：{Path}")]
    private static partial void LogCacheUnwritable(ILogger logger, string path, Exception exception);
}
