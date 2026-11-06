using System.Buffers;

namespace Ntm.Protocol.Sse;

/// <summary>
/// 把上游的字节流切成 SSE 帧。它只读不改：透传路径把原始字节直接转发给客户端，
/// 同一份字节另外喂给这个嗅探器提取信息（ADR-0002）。所以它不需要还原字节，只需要认出字段。
/// </summary>
/// <remarks>
/// 用法是喂一块、读干净：
/// <code>
/// sniffer.Append(chunk);
/// while (sniffer.TryReadFrame())
/// {
///     // 用 sniffer.EventName / sniffer.Data，它们指向内部缓冲，下一次 TryReadFrame 前有效
/// }
/// </code>
/// </remarks>
public sealed class SseFrameSniffer : IDisposable
{
    /// <summary>单帧上限。超过就停止嗅探，但不影响转发——字节已经在去客户端的路上了。</summary>
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    private readonly int _maxFrameBytes;

    private byte[] _buffer;
    private int _bufferLength;
    private int _scanned;
    private long _bufferStartOffset;

    private byte[] _data;
    private int _dataLength;

    private byte[] _eventName;
    private int _eventNameLength;

    public SseFrameSniffer(int maxFrameBytes = DefaultMaxFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1024);

        _maxFrameBytes = maxFrameBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        _data = ArrayPool<byte>.Shared.Rent(4 * 1024);
        _eventName = ArrayPool<byte>.Shared.Rent(64);
    }

    /// <summary>当前帧的 <c>event:</c> 字段，没有就是空。</summary>
    public ReadOnlySpan<byte> EventName => _eventName.AsSpan(0, _eventNameLength);

    /// <summary>当前帧的 <c>data:</c> 字段，多行按 SSE 规范用 <c>\n</c> 连接。</summary>
    public ReadOnlySpan<byte> Data => _data.AsSpan(0, _dataLength);

    /// <summary>是否因为单帧超过上限而放弃嗅探。此后 Usage 只能标为不可得。</summary>
    public bool Abandoned { get; private set; }

    /// <summary>
    /// 当前帧在整条上游字节流里的起始偏移（含帧内的注释行）。
    /// 需要从流里剔掉某一帧时（ADR-0002：吞掉我们自己注入的 usage 块），
    /// 转发方按这个区间切，剩下的字节仍然原样转发。
    /// </summary>
    public long FrameStartOffset { get; private set; }

    /// <summary>当前帧（连同收尾空行）在整条上游字节流里的结束偏移，右开。</summary>
    public long FrameEndOffset { get; private set; }

    /// <summary>已经被切走的字节总数，也就是缓冲区首字节在整条流里的偏移。</summary>
    public long ConsumedOffset => _bufferStartOffset;

    /// <summary>喂入上游的一块字节。</summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (Abandoned || chunk.IsEmpty)
        {
            return;
        }

        if (_bufferLength + chunk.Length > _maxFrameBytes)
        {
            // 缓冲里是还没凑齐边界的半帧。到这一步说明上游在发一个超大帧，或者根本不是 SSE。
            Abandoned = true;
            _bufferLength = 0;
            _scanned = 0;
            return;
        }

        EnsureCapacity(ref _buffer, _bufferLength + chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_bufferLength));
        _bufferLength += chunk.Length;
    }

    /// <summary>取出下一个完整帧。返回 false 表示缓冲里还没有完整帧。</summary>
    public bool TryReadFrame()
    {
        if (Abandoned)
        {
            return false;
        }

        while (true)
        {
            int boundary = FindFrameBoundary(out int boundaryLength);
            if (boundary < 0)
            {
                return false;
            }

            ReadOnlySpan<byte> frame = _buffer.AsSpan(0, boundary);
            bool hasFields = ParseFrame(frame);

            // 偏移要在切走之前记：起点就是缓冲区首字节在整条流里的位置。
            FrameStartOffset = _bufferStartOffset;
            FrameEndOffset = _bufferStartOffset + boundary + boundaryLength;

            Consume(boundary + boundaryLength);

            // 纯注释帧（`: keepalive`）和空帧不算帧，继续往后找。
            if (hasFields)
            {
                return true;
            }
        }
    }

    /// <summary>流结束时，缓冲里可能还剩一个没有收尾空行的帧。</summary>
    public bool TryReadFinalFrame()
    {
        if (Abandoned || _bufferLength == 0)
        {
            return false;
        }

        bool hasFields = ParseFrame(_buffer.AsSpan(0, _bufferLength));
        FrameStartOffset = _bufferStartOffset;
        FrameEndOffset = _bufferStartOffset + _bufferLength;
        _bufferStartOffset += _bufferLength;
        _bufferLength = 0;
        _scanned = 0;
        return hasFields;
    }

    private bool ParseFrame(ReadOnlySpan<byte> frame)
    {
        _dataLength = 0;
        _eventNameLength = 0;
        bool hasFields = false;

        while (!frame.IsEmpty)
        {
            int lineEnd = frame.IndexOfAny((byte)'\n', (byte)'\r');
            ReadOnlySpan<byte> line;
            if (lineEnd < 0)
            {
                line = frame;
                frame = default;
            }
            else
            {
                line = frame[..lineEnd];
                int skip = lineEnd + 1;
                if (frame[lineEnd] == (byte)'\r' && skip < frame.Length && frame[skip] == (byte)'\n')
                {
                    skip++;
                }

                frame = frame[skip..];
            }

            if (line.IsEmpty || line[0] == (byte)':')
            {
                // 注释行。SSE 的心跳就是这个形状，客户端会忽略它。
                continue;
            }

            int colon = line.IndexOf((byte)':');
            ReadOnlySpan<byte> field = colon < 0 ? line : line[..colon];
            ReadOnlySpan<byte> value = colon < 0 ? default : line[(colon + 1)..];
            if (!value.IsEmpty && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            if (field.SequenceEqual("data"u8))
            {
                if (_dataLength > 0)
                {
                    EnsureCapacity(ref _data, _dataLength + 1);
                    _data[_dataLength++] = (byte)'\n';
                }

                EnsureCapacity(ref _data, _dataLength + value.Length);
                value.CopyTo(_data.AsSpan(_dataLength));
                _dataLength += value.Length;
                hasFields = true;
            }
            else if (field.SequenceEqual("event"u8))
            {
                EnsureCapacity(ref _eventName, value.Length);
                value.CopyTo(_eventName);
                _eventNameLength = value.Length;
                hasFields = true;
            }

            // id 与 retry 对嗅探没用，跳过。
        }

        return hasFields;
    }

    private int FindFrameBoundary(out int boundaryLength)
    {
        // 从上次扫到的位置继续，别每来一块就重扫全部。回退一个字节以覆盖跨块的 \r\n\r\n。
        int from = Math.Max(0, _scanned - 3);
        ReadOnlySpan<byte> span = _buffer.AsSpan(0, _bufferLength);

        for (int i = from; i < span.Length; i++)
        {
            byte c = span[i];
            if (c != (byte)'\n' && c != (byte)'\r')
            {
                continue;
            }

            int afterFirst = i + 1;
            if (c == (byte)'\r' && afterFirst < span.Length && span[afterFirst] == (byte)'\n')
            {
                afterFirst++;
            }

            if (afterFirst >= span.Length)
            {
                break;
            }

            byte next = span[afterFirst];
            if (next != (byte)'\n' && next != (byte)'\r')
            {
                continue;
            }

            int afterSecond = afterFirst + 1;
            if (next == (byte)'\r' && afterSecond < span.Length && span[afterSecond] == (byte)'\n')
            {
                afterSecond++;
            }
            else if (next == (byte)'\r' && afterSecond >= span.Length)
            {
                // \r 可能是 \r\n 的前半，等下一块再判。
                break;
            }

            _scanned = 0;
            boundaryLength = afterSecond - i;
            return i;
        }

        _scanned = _bufferLength;
        boundaryLength = 0;
        return -1;
    }

    private void Consume(int count)
    {
        _bufferStartOffset += count;
        _bufferLength -= count;
        if (_bufferLength > 0)
        {
            _buffer.AsSpan(count, _bufferLength).CopyTo(_buffer);
        }

        _scanned = 0;
    }

    private static void EnsureCapacity(ref byte[] array, int required)
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
        array.CopyTo(grown, 0);
        ArrayPool<byte>.Shared.Return(array);
        array = grown;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<byte>.Shared.Return(_data);
        ArrayPool<byte>.Shared.Return(_eventName);
        _buffer = [];
        _data = [];
        _eventName = [];
        _bufferLength = 0;
        _dataLength = 0;
        _eventNameLength = 0;
    }
}
