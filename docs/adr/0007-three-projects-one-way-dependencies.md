# 三个项目，依赖单向

Ntm 分三个 .NET 项目。`Ntm.Protocol` 只依赖 BCL，做 IR、三种 ApiFormat 的解析与序列化、SSE 帧切分、ContentEvent 判定、Usage 提取。`Ntm.Gateway` 依赖 Protocol，做选路、冷却、限制、亲和、Attempt 循环、计费，以及两个 DbContext 与迁移。`Ntm.Server` 依赖两者，只做宿主：Program.cs、端点、鉴权、DI、wwwroot。依赖方向单向，没有东西依赖 Server。

关键约束是 Protocol 不引用 ASP.NET 也不引用 EF Core。协议转换是最容易出 bug、最需要大量用例的部分，它的测试必须是毫秒级的纯函数测试，不能要求起 host 或连数据库。这条约束就是这个划分的全部价值。

不单独拆 `Ntm.Data`：只有一个数据库提供程序，拆出来只是多一层间接。

## Consequences

- Protocol 里任何对 HttpContext、IHeaderDictionary、DbContext 的需求都是设计信号：那段逻辑属于 Gateway。
- Gateway 带 Microsoft.AspNetCore.App 框架引用（Attempt 循环要直接写 HttpResponse），但不注册端点。
- 领域术语表对应 Protocol 与 Gateway 两个上下文；Server、apps/web、apps/docs 用 Gateway 的语言，不持有自己的术语表。
