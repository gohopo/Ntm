namespace Ntm.Protocol.Ir;

/// <summary>消息的发言方。system 不是消息，单独放在 <see cref="IrRequest.System"/>。</summary>
public enum IrRole
{
    User = 1,
    Assistant = 2,
}

/// <summary>
/// 缓存断点的有效期（Anthropic <c>cache_control</c> 的 <c>ttl</c>）。没有断点就是 null。
/// </summary>
public enum IrCacheTtl
{
    FiveMinutes = 1,
    OneHour = 2,
}

/// <summary>
/// 消息内容的最小单位（ContentBlock）。一条消息的内容是有序的块数组，顺序本身是语义的一部分：
/// 推理、文字、工具调用的交错顺序要能原样记下来（ADR-0005）。
/// </summary>
public abstract record IrBlock
{
    /// <summary>客户端在这个块上写的缓存断点（CacheBreakpoint，Q242）。只有写 Anthropic 请求时用得上。</summary>
    public IrCacheTtl? Cache { get; init; }
}

/// <summary>
/// 文字。<paramref name="IsRefusal"/> 表示这段文字是模型的拒答：OpenAI Chat 和 Responses 有单独的拒答字段，
/// 写回去时要还原；Anthropic 没有，照普通文字写（Q193）。
/// <paramref name="LogprobsJson"/> 是上游给的 logprobs 原始 JSON 数组，只在 OpenAI Chat 与 Responses 之间搬运。
/// </summary>
public sealed record IrText(string Text, bool IsRefusal = false, string? LogprobsJson = null) : IrBlock;

/// <summary>图片。要么是 base64 数据，要么是一个 URL。</summary>
public sealed record IrImage(string? MediaType, string? Base64Data, Uri? Url) : IrBlock;

/// <summary>文件（如 PDF），base64 数据。</summary>
public sealed record IrFile(string MediaType, string Base64Data, string? FileName) : IrBlock;

/// <summary>模型发起的一次工具调用。参数是原始 JSON 文本，网关不解释它。</summary>
public sealed record IrToolCall(string Id, string Name, string ArgumentsJson) : IrBlock;

/// <summary>客户端回传的工具执行结果。</summary>
public sealed record IrToolResult(string ToolCallId, IReadOnlyList<IrBlock> Content, bool IsError) : IrBlock;

/// <summary>
/// 推理内容。Signature 是上游为自己产出的推理签发的不透明凭证，只有签发方能验证，
/// 网关只原样保存，从不伪造（AxonHub 用随机 UUID 充当占位签名，这里不学）。
/// Anthropic 的 signature、OpenAI Responses 的 <c>encrypted_content</c> 都放在这里。
/// <paramref name="Producer"/> 记下是哪种 ApiFormat 签的，<paramref name="ItemId"/> 是产出方给这条推理的 id
/// （Responses 的 <c>rs_…</c>）；写出时只有格式相符才带上签名和 id，否则只留文字（ADR-0005、Q186）。
/// </summary>
public sealed record IrReasoning(string Text, string? Signature, ApiFormat? Producer = null, string? ItemId = null) : IrBlock
{
    /// <summary>这条推理的签名能不能交给 <paramref name="format"/> 的上游。来源不明的签名只当作 Anthropic 的（历史行为）。</summary>
    public bool SignedFor(ApiFormat format) =>
        Signature is { Length: > 0 } && (Producer ?? ApiFormat.Anthropic) == format;
}

/// <summary>被上游遮蔽的推理，只有不透明数据。</summary>
public sealed record IrRedactedReasoning(string Data) : IrBlock;

public sealed record IrMessage(IrRole Role, IReadOnlyList<IrBlock> Content);

/// <summary>客户端声明的一个工具。参数 schema 是原始 JSON 文本。</summary>
public sealed record IrTool(string Name, string? Description, string ParametersSchemaJson)
{
    /// <summary>客户端在这个工具定义上写的缓存断点。</summary>
    public IrCacheTtl? Cache { get; init; }
}

public enum IrToolChoiceMode
{
    /// <summary>模型自己决定。</summary>
    Auto = 1,

    /// <summary>必须调用某个工具。</summary>
    Any = 2,

    /// <summary>不许调用工具。</summary>
    None = 3,

    /// <summary>必须调用指定的工具。</summary>
    Specific = 4,
}

public sealed record IrToolChoice(IrToolChoiceMode Mode, string? Name, bool DisableParallelToolUse);

public enum IrResponseFormatKind
{
    /// <summary>只要合法 JSON。</summary>
    JsonObject = 1,

    /// <summary>按给定的 JSON Schema 输出。</summary>
    JsonSchema = 2,
}

/// <summary>结构化输出的要求。OpenAI Chat 的 <c>response_format</c>、Responses 的 <c>text.format</c>；Anthropic 没有（ADR-0014）。</summary>
public sealed record IrResponseFormat(IrResponseFormatKind Kind, string? Name, string? Description, string? SchemaJson, bool? Strict);

/// <summary>一次对话类请求在网关内部的统一形状（IR）。只覆盖 RequestKind.Chat。</summary>
public sealed class IrRequest
{
    public required string Model { get; init; }

    /// <summary>system 提示，按出现顺序。</summary>
    public IReadOnlyList<string> System { get; init; } = [];

    /// <summary>
    /// 和 <see cref="System"/> 一一对应的缓存断点；比 <see cref="System"/> 短时，缺的那些没有断点。
    /// </summary>
    public IReadOnlyList<IrCacheTtl?> SystemCache { get; init; } = [];

    public required IReadOnlyList<IrMessage> Messages { get; init; }

    public IReadOnlyList<IrTool> Tools { get; init; } = [];

    public IrToolChoice? ToolChoice { get; init; }

    public long? MaxOutputTokens { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public IReadOnlyList<string> StopSequences { get; init; } = [];

    public bool Stream { get; init; }

    /// <summary>客户端标识的最终用户，原样转给上游做滥用追踪。</summary>
    public string? EndUserId { get; init; }

    /// <summary>结构化输出。为空表示自由文本。</summary>
    public IrResponseFormat? ResponseFormat { get; init; }

    /// <summary>要 logprobs 时每个位置返回几个候选；0 表示只要选中的那个，为空表示不要。</summary>
    public int? Logprobs { get; init; }

    /// <summary>
    /// 推理强度原文（none、minimal、low、medium、high、xhigh、max 等）。OpenAI Chat 与 Responses 之间照搬；
    /// 转到 Anthropic 时由 <see cref="Anthropic.AnthropicThinking"/> 换成思考参数（ADR-0021）。
    /// Anthropic 客户端的 <c>output_config.effort</c> 也放在这里。
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// 思考预算（token）。只有 Anthropic 客户端用 <c>thinking.type=enabled</c> 时才有；
    /// 单独存着是为了转回预算写法的上游时不用按档位换算、不失真（ADR-0021）。
    /// </summary>
    public long? ThinkingBudget { get; init; }

    /// <summary>客户端明确关掉了思考（Anthropic 的 <c>thinking.type=disabled</c>）。</summary>
    public bool ThinkingDisabled { get; init; }
}

/// <summary>模型停下来的原因。</summary>
public enum IrStopReason
{
    EndTurn = 1,
    MaxTokens = 2,
    StopSequence = 3,
    ToolUse = 4,

    /// <summary>上游内容过滤或拒答。</summary>
    Refusal = 5,
}

/// <summary>一次非流式响应的统一形状。</summary>
public sealed class IrResponse
{
    public string? Model { get; init; }

    public required IReadOnlyList<IrBlock> Content { get; init; }

    /// <summary>上游没给时为空，由写出方按内容推断。</summary>
    public IrStopReason? StopReason { get; init; }

    public Usage Usage { get; init; }
}
