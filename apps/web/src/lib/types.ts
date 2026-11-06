// 和 src/Ntm.Server/Admin/*Endpoints.cs 里的视图一一对应。枚举在 JSON 里是 camelCase 字符串。

export type ApiFormat = 'openAiChat' | 'openAiResponses' | 'anthropic';
export type RequestKind = 'chat' | 'embedding' | 'image' | 'audio' | 'rerank';
export type MatchKind = 'exact' | 'regex';
export type UpstreamModelSource = 'manual' | 'synced';
export type UsageSource = 'unavailable' | 'upstream' | 'gatewayEstimated';
/** Request 从哪来：客户端拿 API 密钥发的，还是 Admin 在 Workbench 里发的（Q208）。 */
export type RequestOrigin = 'client' | 'workbench';
export type RequestStatus = 'running' | 'succeeded' | 'failed' | 'canceled' | 'aborted';
export type CooldownScope = 'none' | 'key' | 'keyModel' | 'channelModel' | 'channel';
export type FailureClass =
	| 'none'
	| 'authFailure'
	| 'quotaExhausted'
	| 'rateLimited'
	| 'modelNotFound'
	| 'upstreamOverloaded'
	| 'upstreamServerError'
	| 'firstEventTimeout'
	| 'idleTimeout'
	| 'connectionFailure'
	| 'softError'
	| 'clientRequestError'
	| 'clientDisconnected'
	| 'interrupted'
	| 'unknown'
	| 'streamInterrupted'
	| 'quotaBlocked'
	| 'restrictionBlocked';

export interface ChannelView {
	id: number;
	name: string;
	enabled: boolean;
	baseUrl: string;
	path: string | null;
	requestUrl: string | null;
	apiFormat: ApiFormat;
	/** 价目目录里的供应商 id；空字符串表示「不在列表里」，没有自动单价。 */
	provider: string;
	priority: number;
	weight: number;
	multiplier: number;
	keyCount: number;
	enabledKeyCount: number;
	upstreamModelCount: number;
	coolingCount: number;
	createdAt: string;
	updatedAt: string;
	/** 出站代理；null 表示直连。密码永远不回传（Q131）。 */
	proxy: ProxyView | null;
	/** 模型同步的开关与最近一次结果（Q135、Q140）。 */
	modelSync: ModelSyncView | null;
	/** Anthropic 渠道用 Authorization: Bearer 带密钥，而不是 x-api-key（Q166）。 */
	anthropicBearerAuth: boolean;
	/** OpenAI 入站转到这个 Anthropic 渠道时，客户端没写断点就由网关加缓存断点（Q247）。 */
	autoCacheBreakpoints: boolean;
	keysShareAccount: boolean;
	/** 推理强度转到这个 Anthropic 渠道时思考参数的写法（ADR-0021）。 */
	thinkingStyle: ThinkingStyle;
	/** 列表里才有：近 30 分钟有样本的上游模型里最低的健康系数；没有样本时为 null（Q261）。 */
	lowestHealth?: number | null;
}

/** 渠道出站代理的显示形式。readable=false 表示存着的密文解不开，渠道因此不参与选路。 */
export interface ProxyView {
	masked: string | null;
	readable: boolean;
}

/** 渠道上的模型同步状态。lastAt 为 null 表示还没同步过。 */
export interface ModelSyncView {
	enabled: boolean;
	lastAt: string | null;
	lastSucceeded: boolean | null;
	lastMessage: string | null;
	lastAdded: number;
	lastRemoved: number;
}

export type ModelSyncEventKind = 'enteredRouting' | 'leftRouting';

/** 一条模型同步事件：某个上游模型进入或离开了选路（Q140）。 */
export interface ModelSyncEventView {
	id: number;
	occurredAt: string;
	kind: ModelSyncEventKind;
	channelId: number;
	channelName: string;
	upstreamModel: string;
	publishedModels: string;
	acknowledged: boolean;
}

export interface ModelSyncEventListView {
	events: ModelSyncEventView[];
	/** 还没点过「知道了」的「进入选路」条数，用在总览的提示上。 */
	unacknowledgedEntered: number;
}

/** 价目目录里的一个供应商（GET /api/price-catalog）。 */
export interface CatalogProviderView {
	id: string;
	name: string;
	modelCount: number;
}

/** 价目目录的状态（Q69/Q70）。fetchedAt 为 null 表示还没取到目录。 */
export interface CatalogStatusView {
	fetchedAt: string | null;
	lastAttemptAt: string | null;
	lastError: string | null;
	autoRefresh: boolean;
	refreshing: boolean;
	providerCount: number;
}

export interface CatalogView {
	status: CatalogStatusView;
	providers: CatalogProviderView[];
}

export type PriceSource = 'auto' | 'manual';

/** 一组单价，美元 / 每百万 Token。null 是「不知道」，0 是免费（Q65）。 */
export interface PriceSetView {
	inputPrice: number | null;
	cacheReadPrice: number | null;
	cacheWrite5mPrice: number | null;
	cacheWrite1hPrice: number | null;
	outputPrice: number | null;
	/** 输出过思考内容时整个输出按它算；null 是「和输出相同」，不是「不知道」（ADR-0013）。 */
	thinkingOutputPrice: number | null;
}

/** 总输入超过 inputTokensOver 时整次按这一组算（Q66）。 */
export interface LongContextPriceView {
	inputTokensOver: number;
	prices: PriceSetView;
}

/** 单价页的一行（Q70/Q74）。 */
export interface PriceRowView {
	id: number;
	provider: string;
	upstreamModel: string;
	source: PriceSource;
	unpriced: boolean;
	matchedModelId: string | null;
	candidates: string[];
	prices: PriceSetView;
	longContext: LongContextPriceView[];
	channels: string[];
	unpricedRequests: number;
	updatedAt: string;
	/** 每张图片的单价（美元）。只能手填，设了就按返回的图片张数算，不看 token（Q230）。 */
	imagePrice: number | null;
}

export interface PricePageView {
	items: PriceRowView[];
	total: number;
	page: number;
	pageSize: number;
}

export interface PriceInput extends PriceSetView {
	longContext: LongContextPriceView[];
	imagePrice: number | null;
}

export interface ChannelKeyView {
	id: number;
	name: string;
	prefix: string;
	enabled: boolean;
	priority: number;
	readable: boolean;
	createdAt: string;
	restrictions: KeyRestrictionsView;
}

/** 一段可用时段。weekdays 用 0–6，周日是 0；时间是 HH:mm，按网关时区。 */
export interface TimeWindowView {
	weekdays: number[];
	start: string;
	end: string;
}

export interface ChannelRestrictionsView {
	dailyCostLimitNanoUsd: number | null;
	timeWindows: TimeWindowView[];
}

export interface KeyRestrictionsView {
	requestsPerMinute: number | null;
	maxConcurrency: number | null;
	allowedModels: string[];
}

export interface ApiKeyRestrictionsView {
	costLimitNanoUsd: number | null;
	maxConcurrency: number | null;
	allowedModels: string[];
	allowedChannelIds: number[];
	blockedChannelIds: number[];
}

export interface KeyRuntimeView {
	id: number;
	requestsThisMinute: number;
	inFlight: number;
}

export interface ChannelRuntimeView {
	timeZone: string;
	spentTodayNanoUsd: number;
	dailyCostLimitNanoUsd: number | null;
	unpricedTodayCount: number;
	inTimeWindow: boolean;
	nextWindowStart: string | null;
	inFlight: number;
	keys: KeyRuntimeView[];
	/** 近 30 分钟有样本的上游模型的健康度（Q261）。 */
	health: HealthSample[];
}

/** 一个（渠道, 上游模型）近 30 分钟计入健康度的成功、失败次数和系数。 */
export interface HealthSample {
	upstreamModel: string;
	successes: number;
	failures: number;
	factor: number;
}

export type BlockReason =
	| 'cooldown'
	| 'dailyCostLimit'
	| 'outsideTimeWindow'
	| 'requestsPerMinute'
	| 'concurrency'
	| 'keyModelWhitelist';

/** 「现在的选路」里的一个候选（Q259）。block 为 null 表示可用；share 只在当前档里有值。 */
export interface RoutingCandidateView {
	channelId: number;
	channelName: string;
	upstreamModel: string;
	channelPriority: number;
	ruleOrdinal: number;
	weight: number;
	successes: number;
	failures: number;
	healthFactor: number;
	effectiveWeight: number;
	share: number | null;
	block: BlockReason | null;
	recoversAt: string | null;
}

export interface RoutingTierView {
	priority: number;
	/** 第一个有可用候选的档：请求现在落在这一档（Q260）。 */
	active: boolean;
	candidates: RoutingCandidateView[];
}

export interface RoutingStateView {
	modelEnabled: boolean;
	at: string;
	tiers: RoutingTierView[];
}

/** 选路时被挡掉的候选（Q263）。 */
export interface SkippedCandidateView {
	channelId: number;
	channelName: string;
	upstreamModel: string;
	reason: BlockReason;
	recoversAt: string | null;
}

export interface ApiKeyRuntimeView {
	spentNanoUsd: number;
	costLimitNanoUsd: number | null;
	unpricedCount: number;
	inFlight: number;
}

/** GET /api/settings/version：版本号来自发版的 git 标签，没打标签的构建是 0.0.0-dev。 */
export interface VersionView {
	version: string;
	commit: string | null;
}

export interface TimeZoneView {
	id: string;
	requested: string | null;
	isFallback: boolean;
	now: string;
	localNow: string;
}

export interface UpstreamModelView {
	name: string;
	source: UpstreamModelSource;
	createdAt: string;
	lastSeenAt: string | null;
}

export interface CooldownView {
	scope: CooldownScope;
	keyPrefix: string | null;
	upstreamModel: string | null;
	until: string;
	failure: FailureClass;
	/** 密钥维度的冷却对应的渠道密钥 id；渠道维度的为 null。 */
	keyId: number | null;
}

export interface ScopedRuleView {
	publishedModelId: number;
	publishedModel: string;
	ordinal: number;
}

export interface ChannelDetailView {
	channel: ChannelView;
	keys: ChannelKeyView[];
	upstreamModels: UpstreamModelView[];
	cooldowns: CooldownView[];
	scopedRules: ScopedRuleView[];
	restrictions: ChannelRestrictionsView;
	/** 渠道的请求改写规则，按顺序执行（Q144–Q152）。 */
	rewriteRules: RewriteRuleView[];
}

export type RewriteTarget = 'header' | 'body';
export type RewriteOperation = 'set' | 'remove' | 'default';

export interface RewriteRuleView {
	target: RewriteTarget;
	operation: RewriteOperation;
	/** 请求头名，或请求体里用点号分隔的字段路径。 */
	path: string;
	/** 设置与补充要写的值：请求体是一段 JSON，请求头是纯文本；删除时为 null。 */
	value: string | null;
}

export interface ChannelInput {
	name: string;
	baseUrl: string;
	path: string | null;
	apiFormat: ApiFormat;
	/** 必填：供应商 id，或 '' 表示「不在列表里」。null 只在还没选的时候出现，提交会被拒。 */
	provider: string | null;
	priority: number;
	weight: number;
	multiplier: number;
	enabled: boolean;
	upstreamModels: string[] | null;
	/** 出站代理：null 不改，'' 清除改为直连，其他值替换（Q131）。 */
	proxyUrl?: string | null;
	/** 模型同步开关：null 表示不改（Q135）。 */
	modelSyncEnabled?: boolean | null;
	/** Anthropic 渠道的认证头：true 用 Authorization: Bearer，false 用 x-api-key（Q166）。 */
	anthropicBearerAuth?: boolean | null;
	autoCacheBreakpoints?: boolean | null;
	keysShareAccount?: boolean | null;
	thinkingStyle?: ThinkingStyle | null;
}

/** 往 Anthropic 渠道写思考参数的写法：自动按 Claude 版本挑、固定预算、固定自适应（ADR-0021）。 */
export type ThinkingStyle = 'auto' | 'budget' | 'adaptive';

export interface ModelView {
	id: number;
	name: string;
	displayName: string | null;
	requestKind: RequestKind;
	enabled: boolean;
	ruleCount: number;
	candidateCount: number;
	problemCount: number;
	createdAt: string;
	updatedAt: string;
}

export interface RuleView {
	ordinal: number;
	kind: MatchKind;
	pattern: string;
	channelId: number | null;
	excludedChannelIds: number[];
	excludedPatterns: string[];
	/** 为空表示沿用渠道自己的。 */
	priority: number | null;
	weight: number | null;
	enabled: boolean;
}

export type RuleInput = Omit<RuleView, 'ordinal'>;

export interface ModelInput {
	name: string;
	displayName: string | null;
	requestKind: RequestKind;
	enabled: boolean;
	rules: RuleInput[];
}

export interface CandidateView {
	channelId: number;
	channelName: string;
	upstreamModel: string;
	weight: number;
	ruleOrdinal: number;
	/** 渠道自己的优先级；和所在档位不同说明规则改了它（Q266）。 */
	channelPriority: number;
}

export interface PriorityGroupView {
	priority: number;
	candidates: CandidateView[];
}

export interface RulePreviewView {
	ordinal: number;
	enabled: boolean;
	effectivePattern: string | null;
	problem: string | null;
	matchCount: number;
}

export interface ExcludedCandidateView {
	channelId: number;
	channelName: string;
	upstreamModel: string;
	reason: string;
}

export interface RoutePreviewView {
	candidateCount: number;
	priorityGroups: PriorityGroupView[];
	rules: RulePreviewView[];
	excluded: ExcludedCandidateView[];
}

export interface ModelDetailView {
	model: ModelView;
	rules: RuleView[];
	route: RoutePreviewView;
}

export interface ApiKeyView {
	id: number;
	name: string;
	prefix: string;
	enabled: boolean;
	createdAt: string;
	expiresAt: string | null;
	lastUsedAt: string | null;
}

export interface CreatedApiKeyView {
	secret: string;
	view: ApiKeyView;
}

export interface ApiKeyDetailView {
	apiKey: ApiKeyView;
	restrictions: ApiKeyRestrictionsView;
}

export interface RequestRowView {
	id: string;
	startedAt: string;
	durationMs: number | null;
	timeToFirstContentMs: number | null;
	publishedModel: string;
	channelName: string | null;
	upstreamModel: string | null;
	apiKeyName: string | null;
	origin: RequestOrigin;
	inboundApiFormat: ApiFormat;
	isStream: boolean;
	status: RequestStatus;
	httpStatusCode: number | null;
	attemptCount: number;
	inputTokens: number;
	cacheReadTokens: number;
	cacheWriteTokens: number;
	outputTokens: number;
	usageSource: UsageSource;
	costNanoUsd: number | null;
	unpriced: boolean;
	clientDisconnected: boolean;
	blockedBy: FailureClass | null;
	requestKind: RequestKind;
	imageCount: number;
}

export interface RequestPageView {
	items: RequestRowView[];
	nextCursor: string | null;
}

export interface AttemptView {
	ordinal: number;
	startedAt: string;
	durationMs: number | null;
	firstEventMs: number | null;
	firstContentMs: number | null;
	channelId: number;
	channelName: string;
	channelKeyPrefix: string;
	upstreamModel: string;
	upstreamApiFormat: ApiFormat;
	requestUri: string;
	passthrough: boolean;
	httpStatusCode: number | null;
	failureClass: FailureClass;
	cooldownScope: CooldownScope;
	cooldownUntil: string | null;
	errorMessage: string | null;
	committed: boolean;
	inputTokens: number;
	cacheReadTokens: number;
	cacheWrite5mTokens: number;
	cacheWrite1hTokens: number;
	outputTokens: number;
	usageSource: UsageSource;
	costNanoUsd: number | null;
	unpriced: boolean;
	multiplierBasisPoints: number;
	longContextOver: number | null;
	inputPrice: number | null;
	cacheReadPrice: number | null;
	cacheWrite5mPrice: number | null;
	cacheWrite1hPrice: number | null;
	/** 实际用到的输出单价；sawThinking 时就是思考档（ADR-0013）。 */
	outputPrice: number | null;
	sawThinking: boolean;
	/** 这次尝试返回的图片张数；只有图片生成有（Q230）。 */
	imageCount: number;
	/** 按张计费时实际用到的每张图片单价；按 token 算或没单价时是 null。 */
	imagePrice: number | null;
	/** 上游给的响应 id（Responses 的 resp_…），没有时为 null。 */
	responseId: string | null;
	/** 进行中请求的尝试：还没结束，结果和用量都是空的（Q112）。 */
	inProgress: boolean;
}

export interface RequestDetailView {
	id: string;
	startedAt: string;
	finishedAt: string | null;
	durationMs: number | null;
	timeToFirstContentMs: number | null;
	publishedModel: string;
	apiKeyId: number | null;
	apiKeyName: string | null;
	origin: RequestOrigin;
	inboundApiFormat: ApiFormat;
	requestKind: RequestKind;
	isStream: boolean;
	status: RequestStatus;
	httpStatusCode: number | null;
	committedAt: string | null;
	clientDisconnected: boolean;
	inputTokens: number;
	cacheReadTokens: number;
	cacheWrite5mTokens: number;
	cacheWrite1hTokens: number;
	outputTokens: number;
	usageSource: UsageSource;
	costNanoUsd: number | null;
	unpriced: boolean;
	imageCount: number;
	attempts: AttemptView[];
	blockedBy: FailureClass | null;
	blockDetail: string | null;
	/** 这个 Request 和它所属 Conversation 之前的落点的关系；null 表示认不出 Conversation 或是旧记录（Q122）。 */
	affinity: AffinityOutcome | null;
	/** 进行中、已经请求了中断但还没停下来。 */
	interruptRequested: boolean;
	/** 选路时被冷却或限制挡掉的候选；平时为空（Q263）。 */
	skipped?: SkippedCandidateView[];
}

export type StatsRange = '24h' | '7d' | '30d' | '90d';

/** 一组汇总数字。比例（成功率、尝试失败率）由前端算。 */
export interface StatsTotalsView {
	requestCount: number;
	successCount: number;
	failureCount: number;
	canceledCount: number;
	unpricedCount: number;
	estimatedCount: number;
	attemptCount: number;
	failedAttemptCount: number;
	inputTokens: number;
	cacheReadTokens: number;
	cacheWrite5mTokens: number;
	cacheWrite1hTokens: number;
	outputTokens: number;
	costNanoUsd: number;
	timeToFirstContentSampleCount: number;
	timeToFirstContentP50Ms: number | null;
	timeToFirstContentP95Ms: number | null;
}

export interface StatsPointView {
	start: string;
	/** 当地时间的标签，直接显示。 */
	label: string;
	inProgress: boolean;
	successCount: number;
	failureCount: number;
	canceledCount: number;
	costNanoUsd: number;
	unpricedCount: number;
}

export interface StatsGroupView {
	key: string;
	name: string;
	deleted: boolean;
	totals: StatsTotalsView;
}

export interface StatsGroupsView {
	publishedModels: StatsGroupView[];
	channels: StatsGroupView[];
	upstreamModels: StatsGroupView[];
	apiKeys: StatsGroupView[];
}

export interface StatsView {
	range: StatsRange;
	timeZone: string;
	granularity: 'hour' | 'day';
	from: string;
	to: string;
	totals: StatsTotalsView;
	points: StatsPointView[];
	groups: StatsGroupsView;
}

/** ASP.NET Core 的 ProblemDetails / ValidationProblemDetails。 */
export interface Problem {
	title?: string;
	status?: number;
	detail?: string;
	errors?: Record<string, string[]>;
}

export type AffinityOutcome = 'new' | 'kept' | 'moved';

/** 实时链路里的一次尝试（Q113）：结果字段在尝试结束前都是 null。 */
export interface LiveAttempt {
	attemptNumber: number;
	channelId: number;
	channelName: string;
	keyPrefix: string;
	upstreamModel: string;
	upstreamApiFormat: ApiFormat;
	requestUri: string;
	startedAt: string;
	finishedAt: string | null;
	failureClass: FailureClass | null;
	httpStatusCode: number | null;
	errorMessage: string | null;
	cooldownScope: CooldownScope | null;
	cooldownUntil: string | null;
}

export interface LiveRequest {
	requestId: string;
	startedAt: string;
	apiKeyId: number | null;
	apiKeyName: string | null;
	origin: RequestOrigin;
	publishedModel: string;
	requestKind: RequestKind;
	isStream: boolean;
	committedAt: string | null;
	interruptRequested: boolean;
	attempts: LiveAttempt[];
}

export type LiveEvent =
	| { type: 'snapshot'; requests: LiveRequest[] }
	| { type: 'requestStarted'; request: LiveRequest }
	| { type: 'attemptStarted'; requestId: string; attempt: LiveAttempt }
	| { type: 'attemptFinished'; requestId: string; attempt: LiveAttempt }
	| {
			type: 'committed';
			requestId: string;
			attemptNumber: number;
			committedAt: string;
			timeToFirstContentMs: number | null;
	  }
	| {
			type: 'requestFinished';
			requestId: string;
			status: RequestStatus;
			finishedAt: string;
			durationMs: number;
			httpStatusCode: number | null;
			inputTokens: number;
			outputTokens: number;
			usageSource: UsageSource;
			costNanoUsd: number | null;
	  };

export interface DebugSwitchView {
	until: string | null;
}

export interface DebugCaptureStatusView {
	until: string | null;
	dropped: number;
	defaultDurationMinutes: number;
	maxDurationMinutes: number;
	retentionHours: number;
}

export type DebugPayloadKind = 'clientRequest' | 'upstreamRequest' | 'upstreamResponse';

export interface DebugPayloadView {
	attemptOrdinal: number;
	kind: DebugPayloadKind;
	capturedAt: string;
	httpStatusCode: number | null;
	headers: string;
	body: string;
	truncated: boolean;
	byteCount: number;
}
