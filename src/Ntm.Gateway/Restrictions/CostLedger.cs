using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ntm.Gateway.Data;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Restrictions;

/// <summary>
/// 费用上限要比的两个数（Q77）：每个渠道今天花了多少、每个 API 密钥一共花了多少。都是乘过倍率的 Cost，
/// 都在内存里（ADR-0003），启动时从汇总表载入（Q32/Q88），不另建余额表——那会是第二本账。
/// 缺单价的请求按 0 计（Q89）。
/// </summary>
public sealed class CostLedger(GatewayTimeZone timeZone, IServiceScopeFactory? scopeFactory = null)
{
    private readonly ConcurrentDictionary<int, DailyCost> _channels = new();
    private readonly ConcurrentDictionary<int, long> _apiKeys = new();

    /// <summary>这个渠道今天（网关时区）花了多少纳美元。日期变了就是 0，懒重置（Q87）。</summary>
    public long ChannelToday(int channelId)
    {
        DateOnly today = timeZone.Today();
        return _channels.TryGetValue(channelId, out DailyCost? daily) ? daily.AmountOn(today) : 0;
    }

    /// <summary>这个 API 密钥终身花了多少纳美元。</summary>
    public long ApiKeyTotal(int apiKeyId) => _apiKeys.TryGetValue(apiKeyId, out long total) ? total : 0;

    /// <summary>
    /// 记一笔已结束的 Request。渠道的那天按 Attempt 的开始时刻算，和汇总表按小时归集用的是同一个时刻，
    /// 重启后载入的数和运行时累加的数才对得上。
    /// </summary>
    public void Record(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (AttemptRecord attempt in record.Attempts)
        {
            if (attempt.CostNanoUsd is not { } cost || cost == 0)
            {
                continue;
            }

            DateOnly day = timeZone.DateOf(attempt.StartedAt);
            _channels.GetOrAdd(attempt.ChannelId, _ => new DailyCost()).Add(day, cost);
            if (record.ApiKeyId is { } apiKeyId)
            {
                _apiKeys.AddOrUpdate(apiKeyId, cost, (_, total) => total + cost);
            }
        }
    }

    /// <summary>
    /// 事后补记一个 Attempt 的费用（background 响应回填，Q277）。和 <see cref="Record"/> 一样按 Attempt 开始的那天算。
    /// </summary>
    public void RecordBackfill(int channelId, int? apiKeyId, DateTimeOffset attemptStartedAt, long cost)
    {
        if (cost == 0)
        {
            return;
        }

        _channels.GetOrAdd(channelId, _ => new DailyCost()).Add(timeZone.DateOf(attemptStartedAt), cost);
        if (apiKeyId is { } id)
        {
            _apiKeys.AddOrUpdate(id, cost, (_, total) => total + cost);
        }
    }

    /// <summary>渠道删掉了：丢掉它的计数（Q106）。</summary>
    public void ForgetChannel(int channelId) => _channels.TryRemove(channelId, out _);

    /// <summary>API 密钥删掉了：丢掉它的计数（Q106）。</summary>
    public void ForgetApiKey(int apiKeyId) => _apiKeys.TryRemove(apiKeyId, out _);

    /// <summary>
    /// 从汇总表载入。渠道今天 = 网关时区今天 0 点所在的那个 UTC 小时起的合计（Q88）；
    /// 半小时时区会多算 0 点前那半小时，宁可多算也不少算。API 密钥 = 整张表的合计。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (scopeFactory is null)
        {
            return;
        }

        DateOnly today = timeZone.Today();
        DateTimeOffset midnight = timeZone.StartOf(today);
        var fromHour = new DateTimeOffset(
            midnight.Year, midnight.Month, midnight.Day, midnight.Hour, 0, 0, TimeSpan.Zero);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        TrafficDbContext db = scope.ServiceProvider.GetRequiredService<TrafficDbContext>();

        var channels = await db.HourlyAggregates
            .Where(a => a.HourUtc >= fromHour && a.ChannelId != 0)
            .GroupBy(a => a.ChannelId)
            .Select(g => new { ChannelId = g.Key, Cost = g.Sum(a => a.CostNanoUsd) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var apiKeys = await db.HourlyAggregates
            .Where(a => a.ApiKeyId != 0)
            .GroupBy(a => a.ApiKeyId)
            .Select(g => new { ApiKeyId = g.Key, Cost = g.Sum(a => a.CostNanoUsd) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in channels)
        {
            _channels.GetOrAdd(row.ChannelId, _ => new DailyCost()).Add(today, row.Cost);
        }

        foreach (var row in apiKeys)
        {
            _apiKeys.AddOrUpdate(row.ApiKeyId, row.Cost, (_, total) => total + row.Cost);
        }
    }

    /// <summary>一个渠道的（日期，金额）。旧日期的花费不加到新的一天上。</summary>
    private sealed class DailyCost
    {
        private readonly Lock _gate = new();
        private DateOnly _day;
        private long _amount;

        public long AmountOn(DateOnly day)
        {
            lock (_gate)
            {
                return _day == day ? _amount : 0;
            }
        }

        public void Add(DateOnly day, long amount)
        {
            lock (_gate)
            {
                if (day > _day)
                {
                    _day = day;
                    _amount = 0;
                }

                if (day == _day)
                {
                    _amount += amount;
                }
            }
        }
    }
}
