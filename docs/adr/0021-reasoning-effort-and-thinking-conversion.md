# 推理强度和 Anthropic 思考参数互相换算，表达不了就降级

OpenAI Chat 的 `reasoning_effort`、Responses 的 `reasoning.effort` 和 Anthropic 的 `thinking` / `output_config.effort` 以前互不相通：OpenAI 客户端转到 Anthropic 渠道时思考直接没了，反过来也一样，只能靠请求改写规则硬写 `thinking`。Anthropic 自己又有两种写法：4.5 及以前只认 `enabled` 加 `budget_tokens`，4.7 及以后只认 `adaptive` 加 `output_config.effort`，4.6 两种都行；第三方 Anthropic 兼容上游普遍只认预算写法。

参考的几家：litellm、ccLoad 用固定预算表，vercel、new-api 按最大输出的百分比；碰到新 Claude 都改发 `adaptive`。踩过的坑：预算没对 `max_tokens` 封顶就 400（litellm#39627），替客户端开了思考却留着 `temperature` 就 400（litellm#33203），反向换算把 `output_config.effort` 丢了，`max` 改成 `xhigh` 后上游又不认。

- **只换请求里的思考参数**（Q288）。响应里的思考内容早就能互转；Anthropic 到 Anthropic 是透传，不碰。不按模型能力表校验。
- **IR 同时装强度和预算**（Q289、Q290）。`ReasoningEffort` 照旧装强度原文，另有 `ThinkingBudget`（只有 Anthropic 客户端会带）和 `ThinkingDisabled`。预算单独存，转回预算写法的上游时原样写，不经档位换算。Anthropic 读入：`enabled` 记预算，`adaptive` 只记 `output_config.effort`，`disabled` 和 `between_tools` 记为关闭。
- **渠道上的「思考参数写法」**（Q291）。Anthropic 格式的渠道可选自动（默认）、预算、自适应。自动从上游模型名读 Claude 版本：4.6 及以后自适应，其余包括认不出的名字一律预算，因为第三方兼容上游只认这种。客户端没给强度就什么都不写，网关不替客户端开思考。
- **强度换预算**（Q292）：minimal 1024、low 2048、medium 8192、high 24576、xhigh 和 max 32768。low、medium、high 和工作台原来用的一样。
- **预算换强度**（Q293）：只出 low、medium、high 三档，所有 OpenAI 模型都认；按相邻两档的几何中点分，≤4096 low，≤16384 medium，更大 high。正向表的三档能原样换回来，xhigh、max 换回来是 high。客户端写了 `output_config.effort` 就照原文。
- **关闭和最低档**（Q294）。none 到预算写法什么都不写（旧模型默认不思考），到自适应写法写 effort low，因为新模型发 `disabled` 会 400。minimal 到自适应写 low。Anthropic 的 `disabled` 到 OpenAI 时不写强度：有的模型不认 none。
- **`max_tokens`**（Q295）。客户端给的上限包含思考，网关照用、不调大，预算取表里的值和「上限减 1024」中小的那个，不到 1024 就不开思考。没给上限时，预算写法补「预算加 4096」，自适应写法补 32768，不开思考时照旧补 4096。
- **采样参数**（Q296）。自适应写法一律不写 `temperature` 和 `top_p`，4.7 及以后的模型给了非默认值就 400。预算写法开了思考时去掉 `temperature`，`top_p` 只在 0.95 到 1 之间保留。
- **不认识的取值**（Q297）。自适应写法照原文写 low 到 max；发到不支持的模型上报的错原样回给客户端。转到 OpenAI 格式时不认识的取值照原文，转到 Anthropic 时当作没给。
- **Responses 不加摘要**（Q298）。Anthropic 客户端的预算转到 Responses 只写 `reasoning.effort`，不加 `reasoning.summary`：没做组织验证的账号请求摘要会报错。
- **降级，不避让**（Q299）。强度只是提示，不影响结果对不对，所以不像 `response_format` 那样按 ADR-0014 绕开候选，也不报错。
- **预算写法开不了思考时不开**（Q311、Q312）。预算写法下 `tool_choice` 只能是 auto 或 none，客户端强制调用工具（`required` 或指定函数）时这次不写 `thinking`。工具循环里（最后一条真正的用户消息之后已经有 assistant 消息），这一轮 assistant 写出来的第一块不是带签名的思考时也不开：OpenAI 客户端传不回签名，开了会 400 或被上游悄悄关掉。自适应写法没有这两条限制，照常写。
- **自适应要摘要**（Q313）。Claude 5 起自适应默认 `display: "omitted"`，只回签名不回文字；网关替 OpenAI 客户端写自适应时加上 `display: "summarized"`。Anthropic 客户端透传的请求不动。
- **猜错写法就换一次**（Q315）。「自动」写法下，上游回 `"thinking.type.enabled" is not supported` 或 `adaptive thinking is not supported` 时，同一把渠道密钥换另一种写法重发一次；请求记录里是两次尝试，第一次按客户端错误记，不冷却、不计入健康度。网关在内存里按渠道和上游模型记住对的写法，之后直接用，重启后重新学。只在网关自己写了思考时重发：渠道固定了写法、改写规则碰了 `thinking` 或 `output_config`、或客户端没要强度，都照转上游的错。版本识别也认不带 `claude-` 的名字，比如 `opus-4-7`、`sonnet-5`。

## Consequences

- OpenAI 客户端在 Anthropic 渠道上终于能开思考；Claude Code 这类 Anthropic 客户端走 OpenAI 渠道时，预算会变成强度。
- 模型名不带 Claude 版本的渠道（别名、第三方）默认先用预算写法；上游其实是新 Claude 时，每个渠道加上游模型第一次请求会多一次往返（被拒后换自适应），之后记住。网关重启后要再学一次。
- 自适应写法丢掉客户端的 `temperature` 和 `top_p`，采样不再可控；这是换来不报 400 的代价。
- Anthropic 客户端转到 Responses 时看不到思考内容（没要摘要，Q314 仍然不加，需要时用请求改写规则写 `reasoning.summary`）；xhigh、max 的预算换回强度只剩 high。
- 预算写法下强制调用工具、或在工具循环中途的请求不思考。Opus 5.5、Sonnet 5.5、Fable 5.1、Mythos 5.1 在自适应下也不接受强制调用工具，而它们不写 `thinking` 也默认自适应，网关绕不开，上游的 400 照转。
- 关掉思考转到 OpenAI 时，上游按它自己的默认强度思考，不一定是零。
- 中途改强度会让 Anthropic 那边消息部分的缓存失效，这是上游的规则。
