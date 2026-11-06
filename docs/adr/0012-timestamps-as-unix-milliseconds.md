# 时间戳用 Unix 毫秒整数存

和 ADR-0009 同一个根因：SQLite 没有原生的时间类型，EF Core 的 SQLite 提供程序把 `DateTimeOffset` 存成带偏移量的文本，官方限制页对这类类型的措辞是「Other operations, however, like comparison and ordering will require evaluation on the client.」而文本比较在偏移量不同时根本不是时间顺序——`2026-10-25 22:00:00+08:00` 按字符串排在 `2026-10-25 15:30:00+00:00` 之后，但它在时间上更早。

请求记录必须能在 SQL 里按时间排序、按区间过滤、按保留期批量删除，所以流量库里所有时间列都经值转换器存成 Unix 毫秒 `long`。配置库的时间列也一并转换，保持一致。

## Consequences

- 存的是瞬间，不是「带时区的时间」。读回来统一是 UTC 偏移，原始偏移量不保留。网关内部只需要瞬间，展示时区由前端决定。
- 排序、区间过滤、保留期删除都能下推到 SQL，不会退化成客户端求值。
- 代价是直接拿 sqlite3 看库时列是一串数字，不可读。排查时需要转换一下。
