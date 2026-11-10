namespace Ntm.Gateway.Pricing;

/// <summary>
/// 只读包装：读出的字节超过上限就抛 <see cref="InvalidDataException"/>（Q359）。
/// 数的是解压之后的字节，所以压缩炸弹也挡得住；Content-Length 只能管压缩前的。
/// </summary>
internal sealed class SizeLimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
        {
            throw new InvalidDataException(TooLarge(maxBytes));
        }

        return read;
    }

    public static string TooLarge(long maxBytes) =>
        $"目录超过了 {maxBytes / (1024 * 1024)} MiB 的上限，没有采用。";
}
