# OpenAI Responses 的上游状态：按落点路由，转换时不让上游存

OpenAI Responses 默认把每次响应存在上游账号里（`store` 默认 true），下一轮可以只发新消息，用 `previous_response_id` 指向上一次。这份状态只存在产出它的那个账号里：换一个渠道，或者同一渠道换一把密钥，这个 id 就不存在了；上游是 OpenAI Chat 或 Anthropic 时更是根本没有这种东西。

网关因此做两件事。

**带 `previous_response_id` 的请求只发给产出那次响应的 Channel 与 ChannelKey。** 网关在内存里记「响应 id → 落点」（和 Affinity 一样不落盘，ADR-0003），这类请求只认那一个候选，而且必须是 Responses 格式。记录过期、网关重启过、或者那个落点正在冷却或被限制，就直接回 400，说明这个 id 在本网关上已经失效、请重发完整上下文。不换候选：别的账号会回「找不到这个响应」，客户端看到的是一个更难懂的错误，还白白记一次上游失败。

**入站不是 Responses、上游是 Responses 时，网关固定发 `store:false`，并要 `include:["reasoning.encrypted_content"]`。** 这种请求的客户端永远不会引用上游存的状态，存着只是在上游账号里堆积数据；推理内容要能在下一轮带回去，就得拿加密内容，因为不存的时候上游没有别的办法认出它。入站本来就是 Responses 时照客户端写的转，存不存是客户端的选择。

## Consequences

- 重启网关后，所有进行中的 `previous_response_id` 链都会断，客户端要重发完整上下文。SDK 的多轮示例多数默认这样用，这是 ADR-0003 选内存状态的代价在这里的具体表现。
- 同一个对话一旦用了 `previous_response_id`，就没有失败切换：它的落点出问题，这一轮就失败。不用它的客户端（每轮发完整上下文）照常享有切换。
- `background`、`conversation`、`item_reference` 这几种依赖上游状态又没法按落点固定的用法，这一轮直接拒绝。
- 后来（ADR-0020）落点存进了流量库，保留 30 天：重启不再让链断掉；`background`、`conversation`、和它们一起用的 `item_reference` 都按落点放行，按 id 的端点也开了。上面「记录过期、网关重启过」那一条因此只剩「过了保留期」。落点正在冷却时仍然回 400，不换候选。
