namespace Ntm.Gateway.Routing;

/// <summary>
/// Nginx 的平滑加权轮询。权重 5:1:1 给出 a a b a c a a 这样的序列，
/// 而不是 a a a a a b c——同样的比例，但不会把五个请求连着压到一个候选上。
///
/// 每次挑选：所有可用候选各自累加自己的权重，取当前值最大的那个，
/// 然后从胜者身上减去权重总和。状态随快照重建而清零，几轮就收敛回来。
/// </summary>
public sealed class SmoothWeightedRoundRobin
{
    private readonly Dictionary<int, long> _current = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// 从可用候选里挑一个。<paramref name="weights"/> 是和 <paramref name="available"/> 等长的生效权重
    /// （已经乘过健康系数）。返回在 <paramref name="available"/> 里的下标；没有可用候选返回 -1。
    /// </summary>
    public int Pick(IReadOnlyList<int> available, IReadOnlyList<int> weights)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(weights);

        if (available.Count == 0)
        {
            return -1;
        }

        if (available.Count != weights.Count)
        {
            throw new ArgumentException("候选与权重的数量必须一致。", nameof(weights));
        }

        if (available.Count == 1)
        {
            return 0;
        }

        lock (_gate)
        {
            long total = 0;
            int bestSlot = 0;
            long bestValue = long.MinValue;

            for (int slot = 0; slot < available.Count; slot++)
            {
                int key = available[slot];
                long weight = Math.Max(0, weights[slot]);
                total += weight;

                long value = _current.GetValueOrDefault(key) + weight;
                _current[key] = value;

                if (value > bestValue)
                {
                    bestValue = value;
                    bestSlot = slot;
                }
            }

            // 全是 0 权重：退化成固定顺序，但仍然返回一个候选，不让请求无处可去。
            if (total == 0)
            {
                return 0;
            }

            _current[available[bestSlot]] = bestValue - total;
            return bestSlot;
        }
    }

    /// <summary>忘掉不再出现的候选的状态。</summary>
    public void Forget(int key)
    {
        lock (_gate)
        {
            _current.Remove(key);
        }
    }
}
