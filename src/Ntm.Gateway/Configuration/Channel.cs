using Ntm.Protocol;

namespace Ntm.Gateway.Configuration;

/// <summary>
/// 一次具体的上游接入配置。持有一个上游地址、一个 Provider、一组可轮换的 ChannelKey、一种 ApiFormat。
/// 它是接入与选路的基本单位。
/// </summary>
public sealed class Channel
{
    public int Id { get; set; }

    /// <summary>唯一。界面与日志里用它指代这个渠道。</summary>
    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>上游 base URL，不含版本前缀（Q10）。</summary>
    public required string BaseUrl { get; set; }

    /// <summary>可选的请求路径，含版本前缀。为空时按 ApiFormat 补默认值。</summary>
    public string? Path { get; set; }

    public ApiFormat ApiFormat { get; set; } = ApiFormat.OpenAiChat;

    /// <summary>
    /// Anthropic 格式的渠道把密钥放进 <c>Authorization: Bearer</c> 而不是 <c>x-api-key</c>（Q166）。
    /// 不少中转只认前者。其他格式忽略这个开关。
    /// </summary>
    public bool AnthropicBearerAuth { get; set; }

    /// <summary>
    /// OpenAI Chat、Responses 入站转到这个 Anthropic 渠道时，客户端一个断点都没写就由网关加缓存断点（Q247，ADR-0019）。
    /// 默认开；只对 Anthropic 格式有意义。客户端自己写的断点不管开关都照转。
    /// </summary>
    public bool AutoCacheBreakpoints { get; set; } = true;

    /// <summary>
    /// 这个渠道的密钥都属于同一个上游账号，互相能读到对方产出的 Responses 响应和对话（Q306，ADR-0020）。
    /// 开了之后 previous_response_id、conversation 和按 id 的接口只钉到渠道，产出它的那把用不了时换同渠道的其他密钥。
    /// 默认关；只对 Responses 格式有意义。
    /// </summary>
    public bool KeysShareAccount { get; set; }

    /// <summary>
    /// 推理强度转到这个 Anthropic 渠道时，思考参数用哪种写法（ADR-0021）。默认自动：按上游模型名里的 Claude 版本挑。
    /// 只对 Anthropic 格式有意义。
    /// </summary>
    public Ntm.Protocol.Anthropic.AnthropicThinkingStyle ThinkingStyle { get; set; }

    /// <summary>
    /// 按谁的价目表计价：外部价目目录里的供应商 id，小写，例如 anthropic、openrouter（Q71）。
    /// 空字符串表示「不在列表里」，这个渠道没有自动单价，只能人工填。
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>候选的档位。高档位先被用尽才轮到低档位，档与档之间不混流。MatchRule 可覆盖。</summary>
    public int Priority { get; set; }

    /// <summary>同一 Priority 档内分流的比例，按平滑加权轮询生效。表达容量，由人配置。MatchRule 可覆盖。</summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// 这个渠道的实际价钱相对官方 Price 的比例，默认 1。中转渠道常见低于 1（Q34）。
    /// 存万分之一，避免浮点：10000 表示 1.0×，3000 表示 0.3×。
    /// </summary>
    public int MultiplierBasisPoints { get; set; } = 10000;

    /// <summary>
    /// 每日费用上限，纳美元（ADR-0009），按乘过倍率的 Cost 算。null 表示不限。「每日」按网关时区的自然日（Q77/Q86）。
    /// 到了上限这个渠道退出选路，但不进入 Cooldown。
    /// </summary>
    public long? DailyCostLimitNanoUsd { get; set; }

    /// <summary>可用时段（Q81）。空表示随时可用；有内容时只在其中某一段里参与选路。</summary>
    public List<ChannelTimeWindow> TimeWindows { get; } = [];

    /// <summary>
    /// 发往这个上游的请求头与 JSON 请求体改写规则（Q144）。空表示不改写。
    /// 有请求体规则时这个渠道的请求体会走 JSON 解析再序列化，不再是定点改写（ADR-0002）。
    /// </summary>
    public List<Rewrite.ChannelRewriteRule> RewriteRules { get; } = [];

    /// <summary>
    /// 可选的出站代理（Q130），整段用 Data Protection 加密：代理地址里常带账号密码（Q131）。
    /// null 表示直连。解不开时这个渠道整个退出选路，绝不悄悄改成直连。
    /// </summary>
    public string? ProtectedProxyUrl { get; set; }

    /// <summary>
    /// 是否定期从上游取回模型清单（Q135）。新渠道默认开；升级前就有的渠道迁移成关，升级不改变它们现在的行为。
    /// </summary>
    public bool ModelSyncEnabled { get; set; } = true;

    /// <summary>最近一次 ModelSync 的结果（Q140）。从没同步过时全部为空。</summary>
    public DateTimeOffset? LastModelSyncAt { get; set; }

    public bool? LastModelSyncSucceeded { get; set; }

    /// <summary>失败原因，或成功时的一句摘要。</summary>
    public string? LastModelSyncMessage { get; set; }

    public int LastModelSyncAdded { get; set; }

    public int LastModelSyncRemoved { get; set; }

    /// <summary>
    /// 这个渠道的 DebugCapture 到期时刻；null 或已过去都表示关。和全局、API 密钥上的开关取或（Q155）。
    /// </summary>
    public DateTimeOffset? DebugCaptureUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChannelKey> Keys { get; } = [];

    /// <summary>这个渠道上游实际提供的模型名。MatchRule 只在这份清单里匹配。</summary>
    public List<UpstreamModel> UpstreamModels { get; } = [];
}

/// <summary>
/// 一段可用时段：一组星期几上的 [开始, 结束) 分钟数，按网关时区（Q81）。
/// 开始大于结束表示跨午夜，属于开始那天；开始等于结束表示全天。
/// </summary>
public sealed class ChannelTimeWindow
{
    /// <summary>星期几的位掩码，第 <see cref="DayOfWeek"/> 位，周日是第 0 位。</summary>
    public int Weekdays { get; set; }

    /// <summary>从 0 点起的分钟数，0–1439。</summary>
    public int StartMinute { get; set; }

    /// <summary>从 0 点起的分钟数，0–1439。</summary>
    public int EndMinute { get; set; }
}
