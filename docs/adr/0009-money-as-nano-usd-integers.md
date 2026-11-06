# 金额用纳美元整数存

EF Core 的 SQLite 提供程序能读写 `decimal`，但官方限制页写得很清楚，只有相等比较受支持：「Other operations, however, like comparison and ordering will require evaluation on the client.」统计页要对费用做 SUM 和 ORDER BY，`decimal` 因此不可用。`double` 在累加大量小额时丢精度，也不行。

所以 Cost 一律存 `long`，单位纳美元（1e-9 USD）。Price 在配置里保持 `decimal`，只在算 Cost 的那一刻换算。

选纳而不是微：便宜模型的单 token 成本在 1e-7 美元量级，微美元会直接舍成 0。`long` 的上限约九十二亿美元，够用。

## Consequences

- 所有费用列、费用上限、汇总列都是 `long` 纳美元，展示层负责格式化。
- 换算只发生在一处：Usage 五项 × Price × 渠道倍率 → 纳美元。
