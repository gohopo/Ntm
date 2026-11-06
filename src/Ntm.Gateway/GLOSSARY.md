# Gateway

Gateway 决定一次客户端请求落到哪个上游、失败后换到哪里、以及这次花了多少钱。它持有渠道与密钥的配置、选路与冷却的运行状态，以及请求记录与统计。

## Language

### 上游接入

**Channel**(渠道):
一次具体的上游接入配置。持有一个上游地址、一个 Provider、一组可轮换的 ChannelKey、一份上游模型清单、一种 ApiFormat，以及可选的出站代理。它是接入与选路的基本单位。Anthropic 格式的 Channel 还决定推理强度转过去时思考参数用哪种写法（ADR-0021）。Channel 可以声明它的 ChannelKey 属于同一个账号，这时 ResponseLanding 只固定 Channel、不固定 ChannelKey（ADR-0020）。
_Avoid_: Upstream, Account, Instance, Provider, 供应商

**ChannelKey**(渠道密钥):
Channel 用来向上游证明身份的凭证。同一个 Channel 下的多个 ChannelKey 互为备份，单个失效不影响其余。同一份密钥值被配到多个 Channel 时，它的 Cooldown 与限速计数是共用的。
_Avoid_: Key, ApiKey, Credential, Secret, Token；ApiKey 是客户端调 Ntm 用的那把，和它反向

**Provider**(供应商):
一个 Channel 的上游按谁的价目表计价，存外部目录里的供应商 id。中转渠道可以填背后的原厂，差价交给 Multiplier；目录里有这个中转商自己时也可以填它自己。
_Avoid_: Vendor, Platform, Brand, Channel, 厂商, 渠道

### 模型下发

**PublishedModel**(下发模型):
Ntm 对客户端公开的一个模型名，是独立实体，不属于任何 Channel。它声明自己的 RequestKind（对话、嵌入或图片），客户端只能从那一类的端点用它；它靠 MatchRule 关联到各 Channel 上的 UpstreamModel，`/v1/models` 返回的就是它的清单。新建时默认不启用。
_Avoid_: Model, Group, Alias, Route, 分组

**UpstreamModel**(上游模型):
某个 Channel 的上游实际提供的模型名，由模型同步取回或人工录入。它和所属 Channel 的 Provider 一起构成 Price 的计价键，也是 Cooldown 里「模型」作用域的标识。
_Avoid_: ActualModel, RealModel, TargetModel, 真实模型

**MatchRule**(匹配规则):
PublishedModel 下的一条规则，决定哪些 Channel 上的哪些 UpstreamModel 可以服务它。匹配方式由规则显式声明为精确名或正则，范围是某个 Channel 或全部 Channel，并可带排除项。匹配到的 UpstreamModel 名就是改写进上游请求的那个名字。一条规则命中多个时全部作为候选，顺序确定；同一个 (Channel, UpstreamModel) 被多条规则命中时先到先得。
_Avoid_: Association, Binding, Mapping, Link, 关联

**ModelSync**(模型同步):
定期从上游取回模型清单，更新某个 Channel 的 UpstreamModel 列表，全量替换但保留人工录入项。每个 Channel 一个开关，用它自己的 ChannelKey 和代理去问上游；取不到清单或清单为空都算失败，这时保留原列表，也不进 Cooldown。它从不创建、也从不启用 PublishedModel；新发现的 UpstreamModel 只有在已有 MatchRule 正好匹配它时才进入选路，这种进出会记成事件给 Admin 看。
_Avoid_: Discovery, Refresh, Fetch, Scan, 拉取

**RewriteRule**(改写规则):
Channel 上的一条规则，改动发给这个上游的请求头或 JSON 请求体，用来补上这个上游要的参数或去掉它不认的参数。操作是设置、删除或补充（只在客户端没给时才写）。认证类请求头、`model` 和 `stream` 改不了：前者会让凭证失效，后者会让选路与计费和实际发出去的请求对不上。它作用在最终发往上游的那一份请求上，所以路径按上游的格式写。
_Avoid_: Transform, Patch, Override, Mutation, 转换, 覆写

### 客户端接入

**ApiKey**(API 密钥):
客户端用来访问 Ntm 的凭证，和 OpenAI 等服务的 API 密钥是同一种东西，客户端把它填在自己「API 密钥」那一栏里。可绑定费用上限、模型限制、渠道黑白名单与并发上限；它的费用上限是终身累计额度，花完为止，不按天重置。完整值只在创建时显示一次，之后只看得到前缀。它和 ChannelKey 方向相反：ApiKey 是别人用来调 Ntm 的，ChannelKey 是 Ntm 用来调上游的，它从不出现在对上游的请求里。
_Avoid_: ApiToken, Token, ClientKey, 令牌, 访问令牌；不要单说 Key 或「密钥」，那会和 ChannelKey 混淆

**Admin**(管理员):
唯一一个登录管理面板、配置渠道与下发模型的人。它和 ApiKey 是两回事：管理员管配置，ApiKey 只用来调用网关，两者的凭证互不通用。
_Avoid_: User, Operator, Account, 用户

**Conversation**(对话):
同一个 ApiKey 对同一个 PublishedModel 发出、且开头内容（system 与第一条消息）相同的一串 Request。Ntm 据此判断多轮对话的延续关系，不依赖客户端传来的标识。
_Avoid_: Session, Thread, Chat, 会话

### 请求

**Request**(请求):
客户端发给 Ntm 的一次调用，从收到到响应结束算一条。它聚合自己所有 Attempt 的结果：最终状态、总费用与 TimeToFirstContent。它带一个来源：客户端，或 Workbench。
_Avoid_: Call, Invocation, Transaction, 调用

**Attempt**(尝试):
一个 Request 内对某个 Channel 的一次上游调用，记录用了哪个 ChannelKey、哪个 UpstreamModel 与耗时。一个 Request 可以有多个 Attempt，Usage 与 Cost 只落在最后那个产生了消耗的 Attempt 上（成功的、已 Commit 的，或客户端断开时仍在进行的），之前被换掉的不计。
_Avoid_: Try, Retry, Round, Hop, 重试

**Commit**(提交):
一个 Request 不可再换候选的时刻：第一个 ContentEvent 已经写给客户端。在它之前失败可以换下一个候选，在它之后失败只能把错误如实告诉客户端。发出响应头不算提交。
_Avoid_: FirstByte, Started, Flushed, 首字节, 已响应

**TimeToFirstContent**(首字时间):
流式 Request 从 Ntm 收到它到 Commit 的时长。非流式 Request 没有这个值。
_Avoid_: TTFB, TTFT, FirstByte, FirstToken, Latency, 首字节时间, 首包时间

**Interrupt**(中断):
Admin 主动停止一个进行中的 Request。它只停，不换候选：客户端收到错误，这次 Request 记为被中断，不算作上游失败，也不让任何东西进入 Cooldown。已经产生的用量照样计费。
_Avoid_: Abort, Cancel, Kill, Stop, 终止, 取消

**DebugCapture**(调试捕获):
为排查问题而临时保存请求原文的开关，可按全局、Channel 或 ApiKey 打开，默认关闭且必须带自动关闭时间。三处任意一处开着就抓。开启时每个 Attempt 存三份：客户端原始请求、实际发给上游的请求、上游原始响应，各自截断。认证类请求头写盘前脱敏，正文不脱敏——正文就是要查的东西，所以它必须默认关、短保留。原文单独存一个库，过期就整批删掉；写入跟不上时宁可丢原文，也不拖慢转发或挤掉 Request 记录。
_Avoid_: Trace, Audit, RawLog, Dump, 抓包

**Workbench**(工作台):
Admin 手动向上游发测试请求的地方。可以固定到一个 Channel 和 UpstreamModel、不走选路，也可以按 PublishedModel 正常选路。它发出的 Request 不属于任何 ApiKey，照常记录、照常计入 Channel 的费用；失败不进 Cooldown，也不影响 Health。固定到 Channel 时不受 Channel 与 ChannelKey 的 Restriction 约束，成功会清掉这次证明可用的 Cooldown（ADR-0017）。
_Avoid_: Playground, Sandbox, 测试台, 调试台

### 选路与容错

**Candidate**(候选):
一个 Request 可以尝试的落点，由一个 Channel 和它上面的一个 UpstreamModel 组成，来自 PublishedModel 的 MatchRule。选路排的是 Candidate，一次 Attempt 是用某个 ChannelKey 去打某个 Candidate。
_Avoid_: Route, Target, Upstream, Member, Item, 目标

**Priority**(优先级):
候选的档位。高档位的 Channel 先被用尽才轮到低档位，档与档之间不混流。Channel 带默认值，MatchRule 可以覆盖。
_Avoid_: Order, Rank, Level, Tier, 权重

**Weight**(权重):
同一个 Priority 档内分流的比例，按平滑加权轮询生效。它表达容量，由人配置，不从密钥数量推算。Channel 带默认值，MatchRule 可以覆盖。
_Avoid_: Share, Ratio, Quota, 配比

**Health**(健康度):
一个 Channel 近期表现出的可用程度，只由已有的请求记录算出，不发额外探测请求。它在同一个 Priority 档内按比例下调 Weight，不跨档改变顺序，且保留下限——完全退出的候选没有机会恢复。按（Channel, UpstreamModel）记近 30 分钟，只算 Channel 本身的问题，只在内存里，网关重启后从零算。
_Avoid_: Score, Reputation, Availability, SuccessRate, 评分

**Affinity**(亲和):
同一个 Conversation 的后续 Request 优先沿用上一次落到的 Channel 与 ChannelKey；那把 ChannelKey 不可用时先换同 Channel 的其他 ChannelKey，整个 Channel 都不可用（Cooldown、Restriction）时才重新选路，之后沿用新的落点。它让位于 Priority：只有沿用的 Channel 仍在当前可用的最高档位里才沿用。目的是让上游的提示缓存与 Signature 留在同一个账号上。OpenAI Responses 请求若用 `previous_response_id` 引用上一次响应，就只能发往产出那次响应的 Channel 与 ChannelKey，那里不可用时直接失败、不重新选路，因为别的账号没有那份状态（ADR-0016）。
_Avoid_: Sticky, StickySession, Pinning, 粘滞

**ResponseLanding**(响应落点):
上游产出的一个响应 id 或对话 id 落在哪里：哪个 Channel、哪把 ChannelKey、哪个 UpstreamModel，以及是哪个 ApiKey 的请求产出的。用 `previous_response_id`、`conversation` 的 Request 和按 id 取回、删除、取消的请求只发往这里，不重新选路（ADR-0016、ADR-0020）。存在流量库里，按最后一次使用保留 30 天；ApiKey 对不上就当作不存在。和 Affinity 不同：Affinity 是「优先沿用」，可以换；ResponseLanding 是「只能去那里」。
_Avoid_: ResponseRoute, Owner, 归属

**UpstreamConversation**(上游对话):
上游 Responses 账号里存着的一份对话（`conv_…`），由客户端经 `/v1/conversations` 建、增删条目，请求带 `conversation` 时上游自己拼上下文。它只存在建它的那个账号里，所以有自己的 ResponseLanding。和 Conversation 不同：Conversation 是网关从请求体推出来的身份，上游不知道它。
_Avoid_: Thread, Session

**Cooldown**(冷却):
一个候选单元因失败而被暂时排除在选路之外的状态。作用域是四者之一：一份 ChannelKey 的密钥值、某个密钥值上的某个模型、某个 Channel 上的某个模型、整个 Channel；具体落在哪一层由错误类别决定。上游给出明确恢复时间时以它为准，否则按错误类别指数退避。到期后先只放一个请求去探，成功才真正放开。
_Avoid_: CircuitBreaker, Ban, Blacklist, Disable

**Restriction**(限制):
Channel、ChannelKey 或 ApiKey 上的配额、时段与名单约束：Channel 有每日成本上限与可用时段，ChannelKey 有 RPM、并发与模型白名单，ApiKey 有费用上限、并发、模型限制与渠道黑白名单。「每日」按网关所在时区的自然日算。Channel 与 ChannelKey 上的触发时让候选退出选路，ApiKey 上的触发时直接拒绝这次 Request；两种都不进入 Cooldown，也不算作上游失败。
_Avoid_: Limit, Quota, Throttle

### 计费

**Price**(单价):
一个 (Provider, UpstreamModel) 对应的一组官方单价，按 Usage 的五项分列，另有一档思考模式的输出单价：模型输出了思考过程时，整个输出都按它计价（ADR-0013）。输入超过某个长度时可换用另一组单价（长上下文单价）。图片生成另有一个每张图片的单价，只能人工填，设了就按返回的张数计价、不看 Token（ADR-0018）。来源是定期从外部目录刷新，可以人工修改，改过的不再被刷新覆盖；单价变动不影响已有记录。
_Avoid_: Pricing, Rate, Tariff, 价格表

**Multiplier**(倍率):
Channel 上的一个系数，表示这个渠道的实际价钱相对官方 Price 的比例，默认 1。中转渠道常见低于 1。每次 Attempt 记下当时的倍率，之后改倍率不影响已有记录。
_Avoid_: Discount, Ratio, Factor, CostMultiplier, 折扣

**Cost**(费用):
一次 Attempt 的 Usage 按各项 Price 相乘求和、再乘 Channel 的 Multiplier 得到的金额，按 (Provider, UpstreamModel) 计价。缺少 Price 时这条记录标为「无单价」，而不是记成 0。费用上限管的是这个数，即真正花出去的钱。
_Avoid_: Billing, Charge, Spend, 花费

**Aggregate**(汇总):
按小时累计的 Request 数、Usage、Cost 与 TimeToFirstContent 的分布，按 Channel、PublishedModel、UpstreamModel 与 ApiKey 分开记。请求记录过了保留期会被删掉，它永久保留，是长期的账。
_Avoid_: Stats, Rollup, Summary, Report, 统计, 统计表
