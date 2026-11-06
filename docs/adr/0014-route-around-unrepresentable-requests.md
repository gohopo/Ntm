# 上游格式表达不了的请求，绕开这个候选

OpenAI Chat 请求转给 Anthropic 上游时，有一类参数在 Anthropic 那边没有对应：`n>1`（要多条回答）、`logprobs`、`response_format`（JSON 模式或 JSON Schema）、音频输出和音频输入。它们会改变结果的含义：客户端要两条回答却只拿到一条，要 JSON 却拿到自由文本，这种错误不报错，只是悄悄给错东西。

可选的做法有三种：转换时丢掉这些参数，回 400，或者让这个候选不接这个请求。丢掉最省事，但客户端以为自己拿到了要的东西；回 400 等于说「这个模型做不到」，可同一个下发模型下往往还有 OpenAI 格式的渠道做得到。所以 Ntm 选第三种：能不能接一个请求不再只看上游格式，还看请求里有什么。带这类参数的请求在选路时跳过 Anthropic 候选，交给能表达它的渠道；一个都没有，回 503，BlockDetail 写明是哪个参数挡住的。

只影响采样细节、不改变结果形状的参数（`seed`、`frequency_penalty`、`presence_penalty`、`logit_bias`、`service_tier`、`store`）照旧在转换时丢掉。为它们绕开候选，代价是大量本来能用的渠道被排除，换来的只是采样分布上的细微差别。

## Consequences

- 同一个请求在不同候选上的结果可能不一样：Anthropic 渠道接了，丢掉了 `seed`；OpenAI 渠道接了，`seed` 生效。这是接受的代价。
- 判断要在选路时做，所以 OpenAI Chat 请求要先完整读一遍才能知道它能不能转给 Anthropic。只在候选里真有 Anthropic 渠道时才读，没有就不付这个成本。
- 以后再加别的上游格式，每种格式都要列自己「表达不了、必须绕开」的参数，不能默认全部丢掉。
- 内置搜索这类由上游执行的工具也按这条处理：OpenAI Chat 的 `web_search_options` 绕开非 Chat 渠道；Anthropic 的服务端工具（没有 `input_schema` 的 tools）和历史里的 `server_tool_use`、`web_search_tool_result` 等块绕开非 Anthropic 渠道。以前 Anthropic 服务端工具在读取时被悄悄跳过，理由是「跳过声明比拒绝整个请求更有用」；现在改成绕开，因为搜不搜会实质改变结果。代价是候选里没有 Anthropic 渠道时，这类请求从「能成功、只是没搜索」变成 503。历史里的这些块以前直接 400，透传也不行，现在透传时原样转发。
