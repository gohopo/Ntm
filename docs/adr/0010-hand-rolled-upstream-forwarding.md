# 自己写转发，不用 YARP

YARP 是 .NET 里做反向代理的标准答案，所以不用它需要解释。

每一个 SSE 帧都要经过三件事：用量嗅探、ContentEvent 判定、序幕缓冲。YARP 的 `StreamCopier` 不提供逐帧钩子，它也没有「这个目标失败了就换下一个目标重试」的能力，而那是网关的核心功能。在 YARP 上做这些等于绕开它的转发路径，剩下能用的只是它的 HttpClient 配置。

于是直接用 `HttpMessageInvoker` 加专用的 `SocketsHttpHandler`，但照抄 YARP 的参数选择：`AutomaticDecompression = None`（字节透传的前提）、`UseProxy = false`、`UseCookies = false`、`AllowAutoRedirect = false`、`EnableMultipleHttp2Connections = true`；上游响应头到达时不调用 `StartAsync`，让重试窗口活到第一个上游字节之后；用 `HttpResponse.HasStarted` 作为重试闩锁。

## Consequences

- 转发路径的正确性归我们自己：必须显式 `DisableBuffering()`、每次写入后 `FlushAsync`、把 `RequestAborted` 串到上游请求上、请求体可重放。
- YARP 的 `ActivityTimeout` 默认一百秒且不被 HTTP/2 ping 续期，长思考会被切断。我们用自己的空闲超时，不继承这个默认值。
