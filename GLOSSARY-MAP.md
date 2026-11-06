# Glossary Map

Ntm 的领域语言分成两个上下文：一个只认 API 格式，一个只认渠道与钱。

## Contexts

- [Protocol](./src/Ntm.Protocol/GLOSSARY.md): 三种 ApiFormat 之间的互转、流事件分类、Usage 提取
- [Gateway](./src/Ntm.Gateway/GLOSSARY.md): 渠道与密钥选路、失败切换与冷却、请求记录与计费

## Relationships

- **Protocol → Gateway**: Protocol 判定一个流事件是不是 ContentEvent，Gateway 据此确定 Commit 时刻与重试窗口的边界
- **Protocol → Gateway**: Protocol 从上游响应里提取 Usage，Gateway 用它算出 Cost 并累加到 ApiKey 与 Channel 的统计上
- **Gateway → Protocol**: Gateway 选定 Channel 后，把入站与上游两种 ApiFormat 一起交给 Protocol；相同走 Passthrough，不同经 IR 转换
- **Protocol → Gateway**: Protocol 声明每种 ApiFormat 支持哪些 RequestKind，Gateway 据此过滤候选，不把嵌入请求送给只会对话的 Channel
- **共享术语**: `ApiFormat`、`Usage`、`RequestKind` 定义在 Protocol，Gateway 直接沿用，不另立同义词

## 不单独立上下文的部分

`src/Ntm.Server`（宿主与端点组装）、`apps/web`（管理前端）、`apps/docs`（文档站）都使用 Gateway 的语言，不单独维护 `GLOSSARY.md`。
