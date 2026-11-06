import type {
	AffinityOutcome,
	ApiFormat,
	BlockReason,
	CooldownScope,
	FailureClass,
	RequestKind,
	RequestStatus,
	ThinkingStyle
} from './types';

const dateTime = new Intl.DateTimeFormat('zh-CN', {
	year: 'numeric',
	month: '2-digit',
	day: '2-digit',
	hour: '2-digit',
	minute: '2-digit',
	second: '2-digit',
	hour12: false
});

const integer = new Intl.NumberFormat('zh-CN');

export function formatTime(value: string | null | undefined): string {
	return value ? dateTime.format(new Date(value)) : '—';
}

export function formatMs(value: number | null | undefined): string {
	if (value === null || value === undefined) {
		return '—';
	}
	return value < 1000 ? `${value} ms` : `${(value / 1000).toFixed(value < 10_000 ? 2 : 1)} s`;
}

export function formatCount(value: number | null | undefined): string {
	return value === null || value === undefined ? '—' : integer.format(value);
}

const money = new Intl.NumberFormat('zh-CN', {
	minimumFractionDigits: 6,
	maximumFractionDigits: 6
});

/** 纳美元 → 固定 6 位小数的美元（Q63）。不是 0 但小于 $0.000001 时显示上界。 */
export function formatCost(nanoUsd: number | null | undefined): string {
	if (nanoUsd === null || nanoUsd === undefined) {
		return '—';
	}
	if (nanoUsd > 0 && nanoUsd < 1000) {
		return '<$0.000001';
	}
	return '$' + money.format(nanoUsd / 1_000_000_000);
}

/** 单价：美元 / 每百万 Token，按配置里填的样子显示。 */
export function formatPrice(value: number | null | undefined): string {
	return value === null || value === undefined ? '无单价' : '$' + value;
}

/** 倍率：10000 个基点 = 1.0×。 */
export function formatMultiplier(basisPoints: number): string {
	return (basisPoints / 10_000).toFixed(basisPoints % 100 === 0 ? 2 : 4) + '×';
}

/** 离现在多久之后（冷却到期这类）。已经过去就说「已到期」。 */
export function formatUntil(value: string): string {
	const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
	if (seconds <= 0) {
		return '已到期';
	}
	if (seconds < 60) {
		return `${seconds} 秒后`;
	}
	if (seconds < 3600) {
		return `${Math.round(seconds / 60)} 分钟后`;
	}
	return `${(seconds / 3600).toFixed(1)} 小时后`;
}

export const apiFormatLabels: Record<ApiFormat, string> = {
	openAiChat: 'OpenAI Chat',
	openAiResponses: 'OpenAI Responses',
	anthropic: 'Anthropic'
};

export const requestKindLabels: Record<RequestKind, string> = {
	chat: '对话',
	embedding: '嵌入',
	image: '图片',
	audio: '音频',
	rerank: '重排'
};

/** 下发模型能选的类型（Q222）。音频和重排还没开。 */
export const publishableKinds: RequestKind[] = ['chat', 'embedding', 'image'];

/** 每类请求的入口，写在提示里。 */
export const requestKindEndpoints: Record<RequestKind, string> = {
	chat: '/v1/chat/completions、/v1/messages、/v1/responses',
	embedding: '/v1/embeddings',
	image: '/v1/images/generations',
	audio: '—',
	rerank: '—'
};

export const statusLabels: Record<RequestStatus, string> = {
	running: '进行中',
	succeeded: '成功',
	failed: '失败',
	canceled: '客户端断开',
	aborted: '被中断'
};

export const failureLabels: Record<FailureClass, string> = {
	none: '—',
	authFailure: '鉴权失败',
	quotaExhausted: '额度用尽',
	rateLimited: '限流',
	modelNotFound: '模型不存在',
	upstreamOverloaded: '上游过载',
	upstreamServerError: '上游服务错误',
	firstEventTimeout: '首字超时',
	idleTimeout: '流空闲超时',
	connectionFailure: '连接失败',
	softError: '软错误',
	clientRequestError: '请求本身有误',
	clientDisconnected: '客户端断开',
	interrupted: '被中断',
	unknown: '未知',
	streamInterrupted: '流中断',
	quotaBlocked: '费用上限用完',
	restrictionBlocked: '被限制挡住'
};

export const cooldownScopeLabels: Record<CooldownScope, string> = {
	none: '—',
	key: '密钥',
	keyModel: '密钥 + 模型',
	channelModel: '渠道 + 模型',
	channel: '整个渠道'
};

/** Affinity 的结果（Q122）。 */
export const affinityLabels: Record<AffinityOutcome, string> = {
	new: 'Conversation 的第一个请求',
	kept: '沿用了上次的渠道和渠道密钥',
	moved: '换了落点'
};

/** 从某个时刻到 now 的经过时间，给进行中的请求用。 */
export function formatElapsed(since: string, now: number): string {
	const ms = Math.max(0, now - new Date(since).getTime());
	if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
	const minutes = Math.floor(ms / 60_000);
	return `${minutes} 分 ${Math.floor((ms % 60_000) / 1000)} 秒`;
}

/** 选路时候选被挡的原因（和后端 RestrictionChecker.Describe 一致）。 */
export const blockReasonLabels: Record<BlockReason, string> = {
	cooldown: '冷却中',
	dailyCostLimit: '今日费用已达上限',
	outsideTimeWindow: '不在可用时段',
	requestsPerMinute: '渠道密钥的每分钟请求数已满',
	concurrency: '渠道密钥的并发已满',
	keyModelWhitelist: '没有渠道密钥允许这个上游模型'
};

/** 健康系数低于这个值时在渠道列表上提醒（Q261）。 */
export const lowHealthThreshold = 0.8;

/** 渠道「思考参数写法」的中文名（ADR-0021）。 */
export const thinkingStyleLabels: Record<ThinkingStyle, string> = {
	auto: '自动',
	budget: '预算',
	adaptive: '自适应'
};
