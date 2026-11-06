using System.Collections.Concurrent;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 候选近期的健康度，只由已有的请求结果算出，不发额外探测请求（规范要求）。
/// 它在同一个 Priority 档内按比例下调 Weight，不跨档改顺序——跨档就等于档位没有意义了，
/// ccLoad 的健康度直接改有效优先级，于是高档位渠道会被挤到低档位后面。
///
/// 下限 0.1：完全退出的候选永远没有机会恢复。样本不足 20 时系数向 1 收敛，
/// 否则一次失败就把一个刚上线的渠道打到最低。
/// </summary>
public sealed class HealthTracker(TimeProvider? timeProvider = null)
{
    private const int WindowMinutes = 30;
    private const int MinConfidentSamples = 20;
    private const double Floor = 0.1;

    private readonly ConcurrentDictionary<(int ChannelId, string UpstreamModel), Buckets> _buckets = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 这类失败算不算渠道的健康问题。密钥维度的问题（认证、额度）、限流、客户端自己的问题都不算：
    /// 它们由冷却处理，计入健康度等于拿运维问题惩罚渠道质量。
    /// </summary>
    public static bool CountsAgainstHealth(FailureClass failure) => failure switch
    {
        FailureClass.None => true,
        FailureClass.UpstreamOverloaded
            or FailureClass.UpstreamServerError
            or FailureClass.FirstEventTimeout
            or FailureClass.IdleTimeout
            or FailureClass.StreamInterrupted
            or FailureClass.ConnectionFailure
            or FailureClass.SoftError
            or FailureClass.ModelNotFound
            or FailureClass.Unknown => true,
        _ => false,
    };

    public void Record(int channelId, string upstreamModel, FailureClass failure)
    {
        ArgumentNullException.ThrowIfNull(upstreamModel);

        if (!CountsAgainstHealth(failure))
        {
            return;
        }

        Buckets buckets = _buckets.GetOrAdd((channelId, upstreamModel), _ => new Buckets());
        buckets.Add(Minute(), failure == FailureClass.None);
    }

    /// <summary>健康系数，落在 [0.1, 1]。</summary>
    public double Factor(int channelId, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(upstreamModel);

        if (!_buckets.TryGetValue((channelId, upstreamModel), out Buckets? buckets))
        {
            return 1;
        }

        (int successes, int failures) = buckets.Totals(Minute());
        return FactorOf(successes, failures);
    }

    private static double FactorOf(int successes, int failures)
    {
        int samples = successes + failures;
        if (samples == 0)
        {
            return 1;
        }

        double successRate = (double)successes / samples;

        // 样本不足时向 1 收敛：confidence 从 0 长到 1。
        double confidence = Math.Min(1.0, (double)samples / MinConfidentSamples);
        double factor = 1 - ((1 - successRate) * confidence);

        return Math.Clamp(factor, Floor, 1);
    }

    /// <summary>近 30 分钟计入健康度的成功、失败次数和由它们算出的系数（Q259/Q261）。只读，给后台看。</summary>
    public HealthSample Sample(int channelId, string upstreamModel)
    {
        ArgumentNullException.ThrowIfNull(upstreamModel);

        if (!_buckets.TryGetValue((channelId, upstreamModel), out Buckets? buckets))
        {
            return new HealthSample(upstreamModel, 0, 0, 1);
        }

        (int successes, int failures) = buckets.Totals(Minute());
        return new HealthSample(upstreamModel, successes, failures, FactorOf(successes, failures));
    }

    /// <summary>这个渠道近 30 分钟有样本的上游模型，按名字排序。</summary>
    public IReadOnlyList<HealthSample> Samples(int channelId)
    {
        long minute = Minute();
        List<HealthSample> samples = [];
        foreach (((int id, string model), Buckets buckets) in _buckets)
        {
            if (id != channelId)
            {
                continue;
            }

            (int successes, int failures) = buckets.Totals(minute);
            if (successes + failures > 0)
            {
                samples.Add(new HealthSample(model, successes, failures, FactorOf(successes, failures)));
            }
        }

        samples.Sort((a, b) => string.CompareOrdinal(a.UpstreamModel, b.UpstreamModel));
        return samples;
    }

    /// <summary>把权重乘上健康系数，整数缩放后交给平滑加权轮询。至少 1。</summary>
    public int EffectiveWeight(int channelId, string upstreamModel, int weight)
    {
        int scaled = (int)Math.Round(Math.Max(1, weight) * 100 * Factor(channelId, upstreamModel));
        return Math.Max(1, scaled);
    }

    private long Minute() => _time.GetUtcNow().ToUnixTimeSeconds() / 60;

    /// <summary>按分钟的环形缓冲，固定 30 格。每格记下它属于哪一分钟，过期的格子读的时候忽略。</summary>
    private sealed class Buckets
    {
        private readonly long[] _minute = new long[WindowMinutes];
        private readonly int[] _successes = new int[WindowMinutes];
        private readonly int[] _failures = new int[WindowMinutes];
        private readonly Lock _gate = new();

        public void Add(long minute, bool success)
        {
            int slot = (int)(minute % WindowMinutes);
            lock (_gate)
            {
                if (_minute[slot] != minute)
                {
                    _minute[slot] = minute;
                    _successes[slot] = 0;
                    _failures[slot] = 0;
                }

                if (success)
                {
                    _successes[slot]++;
                }
                else
                {
                    _failures[slot]++;
                }
            }
        }

        public (int Successes, int Failures) Totals(long minute)
        {
            long oldest = minute - WindowMinutes + 1;
            int successes = 0;
            int failures = 0;

            lock (_gate)
            {
                for (int slot = 0; slot < WindowMinutes; slot++)
                {
                    if (_minute[slot] >= oldest && _minute[slot] <= minute)
                    {
                        successes += _successes[slot];
                        failures += _failures[slot];
                    }
                }
            }

            return (successes, failures);
        }
    }
}

/// <summary>一个（渠道, 上游模型）近 30 分钟的健康度。没有样本时系数为 1。</summary>
public sealed record HealthSample(string UpstreamModel, int Successes, int Failures, double Factor);
