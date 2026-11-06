# 协议转换走规范中间表示

Ntm 要在 OpenAI Chat、OpenAI Responses、Anthropic 三种 API 格式之间互转。两两直转需要 6 条非流式路径加 6 条流式路径，每新增一种 API 格式翻倍。我们改为每种 API 格式只写一个入站解析器和一个出站序列化器，中间经过一个规范中间表示（IR），新增 API 格式的成本从 2N 条路径降到 2 条。

IR 必须容纳三家的并集：工具调用、thinking 块与签名、图片与文件、缓存读写计量、停止原因。凡 IR 表达不了的API 格式专有字段，放进 IR 上的 `Extensions` 袋子原样搬运，避免转换过程静默吃掉字段。

## Consequences

- IR 的字段集就是网关能理解的上限。任何一家新增的能力，在 IR 补字段之前只能走 `Extensions`。
- 同协议透传不经过 IR，见 ADR-0002。
