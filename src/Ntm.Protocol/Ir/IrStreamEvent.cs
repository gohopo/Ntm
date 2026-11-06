using System.Buffers;

namespace Ntm.Protocol.Ir;

/// <summary>
/// 流式回答里一个内容块的种类。形状照 Anthropic 的 content_block 走：它是三种格式里最显式的一种（ADR-0015）。
/// </summary>
public enum IrStreamBlockKind
{
    Text = 1,

    /// <summary>模型的拒答。OpenAI Chat 和 Responses 有单独字段，Anthropic 当普通文字写。</summary>
    Refusal = 2,

    Reasoning = 3,

    /// <summary>被上游遮蔽的推理，只有不透明数据。</summary>
    RedactedReasoning = 4,

    ToolCall = 5,
}

/// <summary>
/// 流式转换的中间事件（ADR-0015）。每种格式只写一个读入器和一个写出器，
/// N×N 个翻译器收成 N 个读 + N 个写。
/// </summary>
public abstract record IrStreamEvent
{
    /// <summary>流开始。上游给了模型名或输入侧用量时一起带上。</summary>
    public sealed record MessageStarted(string? Model, Usage InputUsage) : IrStreamEvent;

    /// <summary>
    /// 一个内容块开始。<paramref name="Index"/> 由读入器按出现顺序编号，同一个块的 delta 和 stop 用同一个号。
    /// 工具调用带上游给的 id 和名字（Responses 的 call_id 放 <paramref name="ToolCallId"/>）。
    /// </summary>
    public sealed record BlockStarted(
        int Index,
        IrStreamBlockKind Kind,
        string? ToolCallId = null,
        string? ToolName = null,
        string? Data = null) : IrStreamEvent;

    /// <summary>块内的一段增量。工具调用块里是参数 JSON 的片段。</summary>
    public sealed record BlockDelta(int Index, IrStreamBlockKind Kind, string Text, string? LogprobsJson = null) : IrStreamEvent;

    public sealed record BlockStopped(int Index, IrStreamBlockKind Kind) : IrStreamEvent;

    /// <summary>上游说完了。用量可能还没来，由写出器在 Complete 时补。</summary>
    public sealed record MessageEnded(IrStopReason? StopReason) : IrStreamEvent;

    /// <summary>上游在流里报了错。</summary>
    public sealed record StreamFailed(string? Type, string Message) : IrStreamEvent;
}

/// <summary>把一种格式的 SSE 数据读成 IR 事件。实现是有状态的，一个流一个实例。</summary>
public interface IIrStreamEventReader
{
    /// <summary>读一帧的 data 部分，把读出来的事件追加到 <paramref name="events"/>。</summary>
    void Read(ReadOnlySpan<byte> data, List<IrStreamEvent> events);

    /// <summary>流结束：补上没关的块和没发出的 MessageEnded。</summary>
    void Finish(List<IrStreamEvent> events);
}

/// <summary>把 IR 事件写成一种格式的 SSE 字节。实现是有状态的，一个流一个实例。</summary>
public interface IIrStreamEventWriter
{
    void Write(IrStreamEvent item, IBufferWriter<byte> output);

    /// <summary>流干净结束：带上最终用量，补齐这个格式要求的收尾帧。</summary>
    void Complete(Usage usage, IBufferWriter<byte> output);

    /// <summary>提交之后上游失败：按客户端格式补一个流内错误帧（ADR-0008）。</summary>
    void WriteError(IBufferWriter<byte> output, string message);
}
