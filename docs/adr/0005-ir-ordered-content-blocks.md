# IR 用有序内容块，用量分互斥项

参考实现（AxonHub）把 IR 做成 OpenAI Chat 的形状：推理挂在消息层字段上，一条消息因此记不住推理、文本、工具调用的交错顺序，只能靠原始元数据绕回去；Usage 沿用 OpenAI「`prompt_tokens` 含缓存」的约定，于是每个非 OpenAI 适配器都要在边缘把缓存项加进去再减出来，个别上游还得靠 `input + 2*cache_read` 这样的补丁修负数。

Ntm 的 IR 改为：消息内容是有序的 ContentBlock 数组（文本、图片、文件、工具调用、工具结果、Reasoning、被屏蔽的 Reasoning），Reasoning 块自带 Signature 与产出它的上游标识；Usage 拆成互不重叠的五项——非缓存输入、缓存读、缓存写 5m、缓存写 1h、输出。选 Chat 形状本来的好处是 OpenAI Chat 近乎恒等转换，但同协议流量走 Passthrough 根本不经过 IR，这个好处拿不到。

## Consequences

- OpenAI Chat 作为入站或上游格式时，有序块在边缘压平成它的形状；交错顺序在那个格式里本来就表达不了。
- Signature 记住了产出方，要不要回放、回放给谁按来源判断，不靠猜字节特征。
- 计费是「每项乘单价再相加」，换算只在边缘做一次。
