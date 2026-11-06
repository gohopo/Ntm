using System.Text.Encodings.Web;
using System.Text.Json;
using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>Anthropic 响应写出时共用的片段。</summary>
internal static class AnthropicWire
{
    /// <summary>中文按原样写出：响应是 JSON / SSE，不会嵌进 HTML。</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string StopReason(IrStopReason reason) => reason switch
    {
        IrStopReason.MaxTokens => "max_tokens",
        IrStopReason.StopSequence => "stop_sequence",
        IrStopReason.ToolUse => "tool_use",
        IrStopReason.Refusal => "refusal",
        _ => "end_turn",
    };

    /// <summary>
    /// Anthropic 的用量本来就是互不重叠的（ADR-0005）：input_tokens 不含缓存，
    /// 两档缓存写入在这个格式里合成一项。
    /// </summary>
    public static void WriteUsage(Utf8JsonWriter writer, Usage usage)
    {
        writer.WriteStartObject("usage");
        writer.WriteNumber("input_tokens", usage.InputTokens);
        writer.WriteNumber("cache_creation_input_tokens", usage.CacheWrite5mTokens + usage.CacheWrite1hTokens);
        writer.WriteNumber("cache_read_input_tokens", usage.CacheReadTokens);
        writer.WriteNumber("output_tokens", usage.OutputTokens);
        writer.WriteEndObject();
    }
}
