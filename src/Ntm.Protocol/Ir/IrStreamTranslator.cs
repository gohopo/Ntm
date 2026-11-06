using System.Buffers;

namespace Ntm.Protocol.Ir;

/// <summary>
/// 一个读入器接一个写出器：上游格式的帧读成 IR 事件，再写成客户端格式（ADR-0015）。
/// 只产出字节，不参与判定：ContentEvent、错误和用量都由上游格式的检视器看。
/// </summary>
public sealed class IrStreamTranslator(IIrStreamEventReader reader, IIrStreamEventWriter writer) : IDisposable
{
    private readonly List<IrStreamEvent> _events = [];

    public bool Completed { get; private set; }

    /// <summary>翻译一帧的 data 字段，把产生的字节追加到 <paramref name="output"/>。</summary>
    public void Translate(ReadOnlySpan<byte> data, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        _events.Clear();
        reader.Read(data, _events);
        Flush(output);
    }

    /// <summary>上游流正常结束：关掉所有块，按客户端格式收尾。</summary>
    public void Complete(Usage usage, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Completed)
        {
            return;
        }

        _events.Clear();
        reader.Finish(_events);
        Flush(output);
        writer.Complete(usage, output);
        Completed = true;
    }

    /// <summary>提交之后上游失败：按客户端格式补一个流内错误帧（ADR-0008）。</summary>
    public void WriteError(IBufferWriter<byte> output, string message) => writer.WriteError(output, message);

    public void Dispose()
    {
        (reader as IDisposable)?.Dispose();
        (writer as IDisposable)?.Dispose();
    }

    private void Flush(IBufferWriter<byte> output)
    {
        foreach (IrStreamEvent item in _events)
        {
            writer.Write(item, output);
        }
    }
}
