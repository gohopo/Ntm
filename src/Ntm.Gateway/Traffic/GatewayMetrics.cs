using Ntm.Gateway.Forwarding;

namespace Ntm.Gateway.Traffic;

/// <summary>
/// /metrics 用的累计计数（Q339）。只在内存里，重启从零开始——Prometheus 的 counter 本来就允许归零。
/// 每个结束的 Request 进记录队列时数一次；不按 API 密钥分标签，免得标签爆炸、也免得把密钥名字带出去。
/// </summary>
public sealed class GatewayMetrics
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RequestKey, long> _requests = [];
    private readonly Dictionary<AttemptKey, long> _attempts = [];
    private readonly Dictionary<string, TokenTotals> _tokens = new(StringComparer.Ordinal);

    public readonly record struct RequestKey(string PublishedModel, RequestStatus Status, RequestOrigin Origin);

    public readonly record struct AttemptKey(int ChannelId, string ChannelName, FailureClass Failure);

    public readonly record struct TokenTotals(long Input, long CacheRead, long CacheWrite, long Output, long CostNanoUsd);

    internal void Observe(RequestRecord record)
    {
        lock (_gate)
        {
            var key = new RequestKey(record.PublishedModel, record.Status, record.Origin);
            _requests[key] = _requests.GetValueOrDefault(key) + 1;

            foreach (AttemptRecord attempt in record.Attempts)
            {
                var attemptKey = new AttemptKey(attempt.ChannelId, attempt.ChannelName, attempt.FailureClass);
                _attempts[attemptKey] = _attempts.GetValueOrDefault(attemptKey) + 1;
            }

            TokenTotals totals = _tokens.GetValueOrDefault(record.PublishedModel);
            _tokens[record.PublishedModel] = new TokenTotals(
                totals.Input + record.InputTokens,
                totals.CacheRead + record.CacheReadTokens,
                totals.CacheWrite + record.CacheWrite5mTokens + record.CacheWrite1hTokens,
                totals.Output + record.OutputTokens,
                totals.CostNanoUsd + (record.CostNanoUsd ?? 0));
        }
    }

    public IReadOnlyList<KeyValuePair<RequestKey, long>> Requests()
    {
        lock (_gate)
        {
            return [.. _requests];
        }
    }

    public IReadOnlyList<KeyValuePair<AttemptKey, long>> Attempts()
    {
        lock (_gate)
        {
            return [.. _attempts];
        }
    }

    public IReadOnlyList<KeyValuePair<string, TokenTotals>> Tokens()
    {
        lock (_gate)
        {
            return [.. _tokens];
        }
    }
}
