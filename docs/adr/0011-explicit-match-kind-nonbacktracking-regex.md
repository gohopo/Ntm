# 匹配方式显式声明，正则用 NonBacktracking

模型名匹配需要正则：这样新同步回来的 UpstreamModel 能被已有 MatchRule 自动接纳，不必手工登记每一个。但正则跑在选路热路径上，有两个风险。

参考实现（AxonHub）两个都踩了。它用启发式猜用户的意图：`strings.ContainsAny(pattern, "*?+[]{}()^$.|\\")` 为真就当正则（`internal/pkg/xregexp/match.go:127-129`）。模型名几乎都带点，于是 `gpt-4.1` 这个看起来像字面量的输入会匹配上 `gpt-4x1`。本地用 .NET 复现：`new Regex("^(?:gpt-4.1)$").IsMatch("gpt-4x1")` 返回 `True`，`Regex.Escape` 之后返回 `False`。另外它用的是回溯引擎，没有匹配超时，模式缓存无上界。

所以：匹配方式由规则显式声明为精确名或正则，不猜。精确名走 `Regex.Escape`。正则用 .NET `Regex` + `RegexOptions.NonBacktracking` + 超时。模式完全锚定，保存时编译校验，非法直接拒绝。

NonBacktracking 的线性时间保证经实测确认：`^(?:(a+)+b)$` 对 40 个 `a` 在 1 毫秒内返回 false，这个模式在回溯引擎上会挂死。代价是它拒绝环视、反向引用和原子组（抛 `NotSupportedException`）；命名组、交替、量化字符类都支持。

## Consequences

- 排除项做成独立字段，而不是让用户写 `(?!...)`——负向环视在这个引擎下不可用。
- 前端不实现匹配逻辑。预览调后端跑生产同一个匹配器，返回命中的 (Channel, UpstreamModel) 对，因此预览不可能和路由不一致。AxonHub 同一个功能有三种正则方言，它自己把浏览器端限制到只能用 `(?i)`。
- 引擎选择是单向的：从 NonBacktracking 放宽到回溯容易，反过来不行——用户一旦写了环视，收紧就是破坏现有配置。
- 运行时编译失败记日志并在面板标红，不静默匹配空集。
