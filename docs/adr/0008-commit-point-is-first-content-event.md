# 提交点是第一个 ContentEvent，不是响应头

客户端无感的故障切换要求「上游在首字节到达客户端之前失败，网关就直接重试下一个候选」。但首字节不是一个有用的边界：SSE 的序幕事件（`response.created`、空的 `message_start`、ping）不携带模型输出，发出去也不妨碍换候选；而响应头一旦发出，状态码就改不了了。

所以提交点定义为：第一个 ContentEvent 已经写给客户端。序幕事件在流开始时缓冲，等第一个 ContentEvent 到达时按原序一并放出。响应头最多压十秒，到点就发头，然后每几秒发一行 `: keepalive` 注释撑住连接，重试窗口继续开着——发头不是提交。

参考实现（ccLoad）把提交点落在第一个非心跳事件上，于是 Anthropic 的 `message_start` 就提交了，之后收到 `overloaded_error` 只能把错误透给客户端；而 OpenAI Responses 的客户端因为 `response.created` 被归为序幕事件，拿到了宽得多的重试窗口。同一套网关给不同格式的客户端不同的容错能力，这是实现细节漏到了行为上。

## Consequences

- 提交之后失败只能在流内发错误事件（Anthropic `event: error`、OpenAI `error` 块），HTTP 状态码已经是 200。openai-python 和 anthropic 的 SDK 会在流内错误上抛异常，手写的 SSE 解析器可能只看到一个短流。
- 非流式响应整体缓冲，发出前不写任何字节，所以它天然原子：要么整体成功，要么还能换候选。
- 哪些事件算序幕要按 ApiFormat 逐个列白名单。这份清单是 Protocol 的职责，Gateway 只读它的判定结果。
