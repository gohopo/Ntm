using System.Buffers;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 从透传的字节流里剔掉我们自己注入 <c>include_usage</c> 换来的那个 usage 块（ADR-0002）。
/// 客户端没要 usage，就不该多收一个 <c>choices: []</c> 的块——有的 SDK 会直接取 <c>choices[0]</c>。
/// </summary>
/// <remarks>
/// 只有这种情况才启用它；其余时候转发方直接转发原始块，零拷贝。
/// 启用时按帧对齐放行：只放出已经完整成帧的字节，半帧留到下一块。
/// SSE 客户端本来就要等到收尾空行才处理一个事件，所以这不会让客户端更晚看到任何东西。
/// 被剔掉的只有那一帧的字节区间，其余字节（包括注释行）原样保留。
/// </remarks>
internal sealed class InjectedUsageFilter : IDisposable
{
    private readonly List<(long Start, long End)> _cuts = [];

    private byte[] _pending = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private int _pendingLength;
    private long _pendingStart;

    private byte[] _output = ArrayPool<byte>.Shared.Rent(16 * 1024);

    /// <summary>记下上游来的一块字节。</summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        EnsureCapacity(ref _pending, _pendingLength, _pendingLength + chunk.Length);
        chunk.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += chunk.Length;
    }

    /// <summary>标记一帧要剔除。区间是整条流里的偏移，右开。</summary>
    public void Cut(long start, long end)
    {
        if (end > start)
        {
            _cuts.Add((start, end));
        }
    }

    /// <summary>
    /// 放出 <paramref name="upTo"/> 之前的字节，跳过被剔除的区间。
    /// 返回的内存在下一次调用前有效。
    /// </summary>
    public ReadOnlyMemory<byte> Release(long upTo)
    {
        long pendingEnd = _pendingStart + _pendingLength;
        upTo = Math.Clamp(upTo, _pendingStart, pendingEnd);

        int count = (int)(upTo - _pendingStart);
        if (count == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        EnsureCapacity(ref _output, 0, count);
        int written = 0;
        long position = _pendingStart;

        for (int i = 0; i < _cuts.Count; i++)
        {
            (long cutStart, long cutEnd) = _cuts[i];
            if (cutStart >= upTo)
            {
                break;
            }

            if (cutStart > position)
            {
                int length = (int)(cutStart - position);
                _pending.AsSpan((int)(position - _pendingStart), length).CopyTo(_output.AsSpan(written));
                written += length;
            }

            position = Math.Max(position, Math.Min(cutEnd, upTo));
        }

        if (upTo > position)
        {
            int length = (int)(upTo - position);
            _pending.AsSpan((int)(position - _pendingStart), length).CopyTo(_output.AsSpan(written));
            written += length;
        }

        _cuts.RemoveAll(c => c.End <= upTo);

        _pendingLength -= count;
        if (_pendingLength > 0)
        {
            _pending.AsSpan(count, _pendingLength).CopyTo(_pending);
        }

        _pendingStart = upTo;
        return new ReadOnlyMemory<byte>(_output, 0, written);
    }

    /// <summary>流结束或嗅探放弃时，把剩下的全部放出去。</summary>
    public ReadOnlyMemory<byte> ReleaseAll() => Release(_pendingStart + _pendingLength);

    private static void EnsureCapacity(ref byte[] array, int preserve, int required)
    {
        if (array.Length >= required)
        {
            return;
        }

        int size = array.Length;
        while (size < required)
        {
            size *= 2;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(size);
        array.AsSpan(0, preserve).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(array);
        array = grown;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_pending);
        ArrayPool<byte>.Shared.Return(_output);
        _pending = [];
        _output = [];
        _pendingLength = 0;
    }
}
