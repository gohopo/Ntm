# Protocol

Protocol 只认 API 格式。它在三种 ApiFormat 的请求、响应和流事件之间互转，判定流事件是否携带模型的真实输出，并从响应里提取 Usage。它不知道 Channel、选路和计费的存在。

## Language

**ApiFormat**(API 格式):
一种 LLM API 的请求与响应形状，取值如 OpenAI Chat、OpenAI Responses、Anthropic。入站和上游各有自己的 ApiFormat，两者不同时需要转换。
_Avoid_: Provider, Protocol, Dialect, Format, Vendor, 方言

**RequestKind**(请求类型):
一次请求要做的事，取值如对话、嵌入、图片、音频、重排。每种 ApiFormat 支持哪些 RequestKind 是固定的，Protocol 声明这张能力表。只有对话类经 IR 转换，其余只在相同 ApiFormat 之间转发；现在开了的是嵌入和图片生成，只在 OpenAI Chat 格式之间透传（ADR-0018）。
_Avoid_: RequestType, Family, Endpoint, Capability, 族

**IR**(中间表示):
一次对话请求、响应或流在 Ntm 内部的统一形状。三种 ApiFormat 都解析成它，也都能从它序列化出去；流是一串按块组织的事件（消息开始、块开始、块内增量、块结束、消息结束、错误），每种格式各有一个读取器和一个写出器（ADR-0015）。只覆盖对话类请求。
_Avoid_: UnifiedRequest, CommonFormat, LlmRequest, 通用格式, 统一格式

**ContentBlock**(内容块):
消息内容的最小单位，在消息里有序排列，取值如文本、图片、文件、工具调用、工具结果、Reasoning、被屏蔽的 Reasoning。顺序本身是语义的一部分。
_Avoid_: Part, Segment, Item, 片段

**Reasoning**(推理):
模型在给出答案前的思考过程，是一种 ContentBlock，可带一个 Signature、产出它的 ApiFormat，以及产出方给它的条目 id。只有回传给同一种 ApiFormat 时才带上 Signature 和条目 id，回传给别的格式只留文字。
_Avoid_: Thinking, ReasoningContent, CoT, 思考, 思维链

**ReasoningEffort**(推理强度):
请求里让模型想多深的提示，取值如 none、minimal、low、medium、high、xhigh、max。OpenAI 两种格式直接带它，Anthropic 写成 ThinkingBudget 或自适应思考的 effort，换算见 ADR-0021。它只是提示，目标格式表达不了就降级，不让候选退出选路。
_Avoid_: Thinking Level, Reasoning Mode, 思考等级

**ThinkingBudget**(思考预算):
Anthropic 预算写法里允许模型用来思考的 token 数，至少 1024，必须小于 max_tokens。只有 Anthropic 客户端会直接给出，网关单独存着，转回预算写法时原样写。
_Avoid_: Reasoning Tokens, Thinking Tokens, 思考上限

**Signature**(签名):
上游为自己产出的 Reasoning 签发的不透明凭证，只有签发方能验证，例如 Anthropic 的 signature、OpenAI Responses 的加密推理内容。Ntm 原样保存，并记住是谁签的。
_Avoid_: Hash, Seal, Nonce, 指纹

**ContentEvent**(内容事件):
流式响应里第一个携带模型真实输出的事件。在它之前的都是序幕：创建、排队、心跳、空内容块、只有 role 的增量。判定规则按 ApiFormat 各有一套。
_Avoid_: FirstEvent, FirstByte, 首个事件, 首字节

**Usage**(用量):
一次上游调用消耗的 LLM 计量，分成互不重叠的五项：非缓存输入、缓存读、缓存写 5m、缓存写 1h、输出。每项各自计价。来源要么是上游给出的精确值，要么是上游没给出时由网关估算的值，两者在记录里可区分。
_Avoid_: Token, Tokens, TokenCount, Credits, PromptTokens

**CacheBreakpoint**(缓存断点):
请求里标出的一个位置，告诉上游把到这里为止的前缀存进提示缓存，带一个存活时间（5 分钟或 1 小时）。Anthropic 用 `cache_control` 写在 tool、system 块或内容块上，一个请求最多 4 个；OpenAI 和 Responses 自动缓存，没有断点。写成 Anthropic 请求时，Protocol 读出客户端写的断点，没有就按固定规则自动放（ADR-0019）。
_Avoid_: CacheMark, CacheControl, Checkpoint, 缓存标记, 缓存点

**Passthrough**(透传):
入站与上游 ApiFormat 相同时的转发方式：只解析出选路需要的字段，再对客户端的原始字节做定点改写，其余原样转发，不经过 IR。
_Avoid_: Proxy, Forward, Relay, Bypass, 直通, 中转
