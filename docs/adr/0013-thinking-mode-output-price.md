# 思考模式按整个输出换一档单价

部分模型在「思考模式」下的输出价格和平时不同。阿里云的文档把规则写得很明确（[深度思考](https://help.aliyun.com/zh/model-studio/deep-thinking) 计费说明）：

> 模型输出了思考过程时，所有输出 Token（包括思考 Token 和回复 Token）均按思考模式的输出价格计费。

所以这**不是**给推理 Token 单独定价，而是整个输出换一档单价，按「这次有没有输出思考过程」二选一。同一份文档也写明触发条件是实际输出，而不是请求里的开关：「若模型在思考模式下未输出思考过程，则按非思考模式价格计费。」文档里就有 `enable_thinking: true` 却返回 `reasoning_tokens: 0` 的例子。

决定如下。

**单价多一列 `ThinkingOutputPrice`**，长上下文档位里也有（qwen-plus 在 256K 以上另有一档思考价）。它是唯一一列 null 不表示「未知」的单价：null 表示「和 `OutputPrice` 相同」。否则所有没有思考价的模型一进思考模式就会变成「无单价」。

**导入时只在 `cost.reasoning` 高于 `output` 时才取它。** models.dev 里这个字段有 34 条与 `output` 不一致，而含义并不统一：阿里系 16 条（`alibaba` 与 `alibaba-cn` 各 8 条）确实是思考模式的整体输出价，约为平时的 3 倍；Sonar deep research 的 6 条是 reasoning 3 对 output 8，那是「推理 Token 单独计价」的另一种规则，Ntm 不实现；kilo 的 10 条是 0，明显是占位数据。只取更高的那些，既筛掉了后两类，也让误判的方向是多收而不是少收。人工填写不受这条限制。

**检测靠上游实际输出的思考内容，不靠 `reasoning_tokens`，也不靠请求里的 `enable_thinking`。** `completion_tokens_details.reasoning_tokens` 在文档里是可选字段，阿里云中英文页面上同一个流式示例一个有、一个没有，不能作为判据。而 `reasoning_content` 本来就是计费规则的触发条件，两个响应检查器也已经在读它（[OpenAiChatResponseInspector.cs](../../src/Ntm.Protocol/OpenAiChat/OpenAiChatResponseInspector.cs)、[OpenAiChatStreamInspector.cs](../../src/Ntm.Protocol/OpenAiChat/OpenAiChatStreamInspector.cs)），只是用完就丢。改成记在 Attempt 上。

**不记推理 Token 数。** 整个输出一个价，它不参与计费；上游又可选，记下来会分不清「没思考」和「没上报」。Attempt 上那个布尔值已经能回答用户真正会问的问题：这次为什么贵。要在界面上展示思考与回答的 Token 拆分时再加。

## Consequences

- 单价的六列里有五列 null 是「未知」（会让记录变成无单价），只有 `ThinkingOutputPrice` 的 null 是「同输出价」。
- 客户端断开后用估算用量时，这个布尔值照样有效：断开前见过思考内容，估出来的输出就按思考价计费。
- 这一列和这个布尔值都会进历史记录，改不动已经落库的 Attempt。
- models.dev 的 `cost.reasoning` 在别家供应商那里可能是另一种含义，再有第三种规则时要重新考虑这条导入过滤，而不是直接放宽它。
