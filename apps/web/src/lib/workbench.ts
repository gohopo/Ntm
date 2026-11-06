// Workbench 的前端逻辑（Q202–Q207、Q214–Q216）：
// 按目标格式拼原生请求体，把客户端会收到的那份字节解析成一份只用于显示的回复。
// 解析器只求容错，不求严格：认不出的事件忽略，原文另有「原始」视图可看。

import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { api, ApiError } from './api.ts';
import type { ApiFormat, RequestDetailView, ThinkingStyle } from './types.ts';

/** 思考等级（Q204）。off 表示不发这个字段，不是发 none。 */
export type Effort = 'off' | 'low' | 'medium' | 'high';

export const effortLabels: Record<Effort, string> = { off: '关', low: '低', medium: '中', high: '高' };

/** Anthropic 三档的思考预算，和网关的换算表一致（ADR-0021）；max_tokens 取预算加 4096。 */
export const anthropicBudgets: Record<Exclude<Effort, 'off'>, number> = { low: 2048, medium: 8192, high: 24576 };

/** 自适应写法补的 max_tokens，和网关一致（ADR-0021）。 */
export const anthropicAdaptiveMaxTokens = 32768;

/** 从模型名读 Claude 版本，规则和网关的 AnthropicThinking.ClaudeVersion 一致。 */
export function claudeVersion(model: string): [number, number] | null {
	const match = /claude-(?:[a-z]+-)?(\d+)(?:[-.](\d{1,2})(?!\d))?/.exec(model.toLowerCase());
	if (!match) return null;
	return [Number(match[1]), match[2] ? Number(match[2]) : 0];
}

/** 把渠道的「自动」落到具体写法：Claude 4.6 及以后自适应，其余预算（ADR-0021）。 */
export function resolveThinkingStyle(style: ThinkingStyle, model: string): 'budget' | 'adaptive' {
	if (style !== 'auto') return style;
	const version = claudeVersion(model);
	return version && (version[0] > 4 || (version[0] === 4 && version[1] >= 6)) ? 'adaptive' : 'budget';
}

/** 不开思考时 Anthropic 必填的 max_tokens（Q216）。 */
export const anthropicDefaultMaxTokens = 1024;

/** 单张图片的上限：三家里最小的单张上限（Anthropic 的 5 MB，Q207）。 */
export const maxImageBytes = 5 * 1024 * 1024;

export interface ImageAttachment {
	/** 例如 image/png。 */
	mediaType: string;
	/** 不带 data: 前缀的 base64。 */
	data: string;
	name: string;
}

export interface Turn {
	role: 'user' | 'assistant';
	text: string;
	images: ImageAttachment[];
}

export interface BuildOptions {
	model: string;
	turns: Turn[];
	stream: boolean;
	effort: Effort;
	search: boolean;
	imageGeneration: boolean;
	/** Anthropic 格式时思考参数的写法；直发渠道时取渠道设置，不给按「自动」。 */
	thinkingStyle?: ThinkingStyle;
}

function dataUrl(image: ImageAttachment): string {
	return `data:${image.mediaType};base64,${image.data}`;
}

/** 按格式拼原生请求体。不发 temperature 这类采样参数（Q216）。 */
export function buildBody(format: ApiFormat, options: BuildOptions): Record<string, unknown> {
	switch (format) {
		case 'openAiChat':
			return buildChat(options);
		case 'openAiResponses':
			return buildResponses(options);
		case 'anthropic':
			return buildAnthropic(options);
	}
}

function buildChat(o: BuildOptions): Record<string, unknown> {
	const body: Record<string, unknown> = {
		model: o.model,
		stream: o.stream,
		messages: o.turns.map((turn) =>
			turn.images.length === 0 || turn.role === 'assistant'
				? { role: turn.role, content: turn.text }
				: {
						role: turn.role,
						content: [
							...(turn.text ? [{ type: 'text', text: turn.text }] : []),
							...turn.images.map((image) => ({ type: 'image_url', image_url: { url: dataUrl(image) } }))
						]
					}
		)
	};
	if (o.stream) body.stream_options = { include_usage: true };
	if (o.effort !== 'off') body.reasoning_effort = o.effort;
	if (o.search) body.web_search_options = {};
	if (o.imageGeneration) body.modalities = ['image', 'text'];
	return body;
}

function buildResponses(o: BuildOptions): Record<string, unknown> {
	const body: Record<string, unknown> = {
		model: o.model,
		stream: o.stream,
		store: false,
		input: o.turns.map((turn) => ({
			role: turn.role,
			content:
				turn.role === 'assistant'
					? [{ type: 'output_text', text: turn.text }]
					: [
							...(turn.text ? [{ type: 'input_text', text: turn.text }] : []),
							...turn.images.map((image) => ({ type: 'input_image', image_url: dataUrl(image) }))
						]
		}))
	};
	if (o.effort !== 'off') body.reasoning = { effort: o.effort, summary: 'auto' };
	const tools: unknown[] = [];
	if (o.search) tools.push({ type: 'web_search' });
	if (o.imageGeneration) tools.push({ type: 'image_generation' });
	if (tools.length > 0) body.tools = tools;
	return body;
}

function buildAnthropic(o: BuildOptions): Record<string, unknown> {
	const body: Record<string, unknown> = {
		model: o.model,
		stream: o.stream,
		max_tokens: anthropicDefaultMaxTokens,
		messages: o.turns.map((turn) => ({
			role: turn.role,
			content: [
				...turn.images.map((image) => ({
					type: 'image',
					source: { type: 'base64', media_type: image.mediaType, data: image.data }
				})),
				...(turn.text ? [{ type: 'text', text: turn.text }] : [])
			]
		}))
	};
	if (o.effort !== 'off') {
		if (resolveThinkingStyle(o.thinkingStyle ?? 'auto', o.model) === 'adaptive') {
			// 自适应写法：4.7 及以后的模型不接受 enabled，也不接受非默认的采样参数（工作台本来就不发）。
			body.thinking = { type: 'adaptive' };
			body.output_config = { effort: o.effort };
			body.max_tokens = anthropicAdaptiveMaxTokens;
		} else {
			const budget = anthropicBudgets[o.effort];
			body.thinking = { type: 'enabled', budget_tokens: budget };
			body.max_tokens = budget + 4096;
		}
	}
	if (o.search) body.tools = [{ type: 'web_search_20250305', name: 'web_search', max_uses: 5 }];
	return body;
}

// ——— 回复 ———

export interface ToolCallView {
	name: string;
	arguments: string;
}

export interface CitationView {
	url: string;
	title: string;
}

/** 嵌入结果的摘要（Q238）：向量本身太长，只给条数、维度和前几个值。 */
export interface EmbeddingSummary {
	count: number;
	dimensions: number;
	head: number[];
	/** 上游按 base64 返回（encoding_format: base64）时，前几个值是解码出来的。 */
	base64: boolean;
}

/** 一条回复里能显示的东西，三种格式、三类请求都解析到同一个形状。 */
export interface Reply {
	text: string;
	reasoning: string;
	images: string[];
	citations: CitationView[];
	toolCalls: ToolCallView[];
	embedding: EmbeddingSummary | null;
	/** 上游或网关的错误说明。 */
	error: string | null;
}

export function emptyReply(): Reply {
	return { text: '', reasoning: '', images: [], citations: [], toolCalls: [], embedding: null, error: null };
}

/** 工作台能测的请求类型（Q238）。 */
export type WorkbenchKind = 'chat' | 'embedding' | 'image';

export const imageSizes = ['1024x1024', '1024x1536', '1536x1024', '512x512'];

export interface KindOptions {
	model: string;
	/** 嵌入的输入文本。 */
	input: string;
	/** 图片的提示语。 */
	prompt: string;
	size: string;
	stream: boolean;
}

/**
 * 嵌入和图片生成的请求体（Q238）：只放最少的字段，n 固定为 1，省得一次测试生成好几张图。
 * 不写 response_format：dall-e 默认回 url，gpt-image 只回 b64_json，各家默认值不一样，交给上游。
 */
export function buildKindBody(kind: Exclude<WorkbenchKind, 'chat'>, options: KindOptions): Record<string, unknown> {
	if (kind === 'embedding') return { model: options.model, input: options.input };
	const body: Record<string, unknown> = { model: options.model, prompt: options.prompt, n: 1 };
	if (options.size) body.size = options.size;
	if (options.stream) body.stream = true;
	return body;
}

function decodeFloat32(base64: string, take: number): { dimensions: number; head: number[] } {
	try {
		const binary = atob(base64);
		const bytes = new Uint8Array(binary.length);
		for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
		const floats = new Float32Array(bytes.buffer, 0, Math.floor(bytes.length / 4));
		return { dimensions: floats.length, head: Array.from(floats.slice(0, take)) };
	} catch {
		return { dimensions: 0, head: [] };
	}
}

type Json = Record<string, unknown>;

function isObject(value: unknown): value is Json {
	return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function str(value: unknown): string {
	return typeof value === 'string' ? value : '';
}

function array(value: unknown): unknown[] {
	return Array.isArray(value) ? value : [];
}

function addCitation(reply: Reply, url: unknown, title: unknown) {
	const href = str(url);
	if (!href || reply.citations.some((c) => c.url === href)) return;
	reply.citations.push({ url: href, title: str(title) || href });
}

function addImage(reply: Reply, src: string) {
	if (src && !reply.images.includes(src)) reply.images.push(src);
}

function imageFromBase64(value: unknown, mediaType = 'image/png'): string {
	const data = str(value);
	if (!data) return '';
	return data.startsWith('data:') || data.startsWith('http') ? data : `data:${mediaType};base64,${data}`;
}

/** 增量地吃进客户端会收到的事件，拼成 Reply。 */
export class ReplyParser {
	readonly reply: Reply = emptyReply();
	/** Chat 的流式工具调用按 index 拼参数。 */
	private chatTools = new Map<number, ToolCallView>();
	/** Anthropic 按 content_block 的 index 记块类型。 */
	private anthropicBlocks = new Map<number, { kind: string; tool?: ToolCallView }>();

	constructor(
		private readonly format: ApiFormat,
		private readonly kind: WorkbenchKind = 'chat'
	) {}

	/** 流式的一条 data。 */
	event(data: Json) {
		if (this.kind === 'image' && str(data.type).endsWith('.partial_failed')) {
			// 火山方舟一组图里单张失败：记下来，别的图照样显示。
			this.reply.error = errorText(data) ?? '有一张图片没生成出来。';
			return;
		}
		if (isObject(data.error)) {
			this.reply.error = errorText(data) ?? '上游在流里报了错。';
			return;
		}
		if (this.kind === 'image') return this.imageEvent(data);
		switch (this.format) {
			case 'openAiChat':
				return this.chatChunk(data);
			case 'openAiResponses':
				return this.responsesEvent(data);
			case 'anthropic':
				return this.anthropicEvent(data);
		}
	}

	/** 非流式的整个响应体。 */
	whole(data: Json) {
		if (this.kind === 'embedding') return this.embeddingWhole(data);
		if (this.kind === 'image') {
			// OpenAI 是 data[]，硅基流动是 images[]；每项给 b64_json 或 url。
			for (const item of [...array(data.data), ...array(data.images)]) {
				if (isObject(item)) addImage(this.reply, imageFromBase64(item.b64_json) || str(item.url));
			}
			for (const item of array(data.data)) {
				if (isObject(item) && str(item.revised_prompt)) this.reply.text = str(item.revised_prompt);
			}
			return;
		}
		switch (this.format) {
			case 'openAiChat':
				for (const choice of array(data.choices)) {
					if (isObject(choice) && isObject(choice.message)) this.chatMessage(choice.message, false);
				}
				return;
			case 'openAiResponses':
				for (const item of array(data.output)) if (isObject(item)) this.responsesItem(item);
				return;
			case 'anthropic':
				for (const block of array(data.content)) if (isObject(block)) this.anthropicBlock(block);
				return;
		}
	}

	private embeddingWhole(data: Json) {
		const items = array(data.data).filter(isObject);
		const first = items[0]?.embedding;
		if (Array.isArray(first)) {
			const values = first.filter((v): v is number => typeof v === 'number');
			this.reply.embedding = { count: items.length, dimensions: values.length, head: values.slice(0, 5), base64: false };
		} else if (typeof first === 'string') {
			const decoded = decodeFloat32(first, 5);
			this.reply.embedding = { count: items.length, ...decoded, base64: true };
		} else {
			this.reply.embedding = { count: items.length, dimensions: 0, head: [], base64: false };
		}
	}

	/** 图片的流式事件：partial_image 是中间稿，completed 是成图；火山方舟是 partial_succeeded。 */
	private imageEvent(data: Json) {
		const type = str(data.type);
		const src = imageFromBase64(data.b64_json, `image/${str(data.output_format) || 'png'}`) || str(data.url);
		if (type.endsWith('.partial_image')) {
			// 中间稿只留最新一张，成图出来后换掉。
			this.partial = src || this.partial;
			if (this.partial) this.reply.images = [...this.finished, this.partial];
			return;
		}
		if (type.endsWith('.completed') || type.endsWith('.partial_succeeded')) {
			if (src) this.finished.push(src);
			this.partial = '';
			this.reply.images = [...this.finished];
		}
	}

	private finished: string[] = [];
	private partial = '';

	private chatMessage(message: Json, delta: boolean) {
		const content = message.content;
		if (typeof content === 'string') this.reply.text += content;
		for (const part of array(content)) {
			if (!isObject(part)) continue;
			if (part.type === 'text') this.reply.text += str(part.text);
			if (part.type === 'image_url' && isObject(part.image_url)) addImage(this.reply, str(part.image_url.url));
		}
		this.reply.reasoning += str(message.reasoning_content) || str(message.reasoning);
		// Gemini / OpenRouter 风格的生图：message.images[].image_url.url（Q206）。
		for (const image of array(message.images)) {
			if (isObject(image) && isObject(image.image_url)) addImage(this.reply, str(image.image_url.url));
		}
		for (const annotation of array(message.annotations)) {
			if (isObject(annotation) && isObject(annotation.url_citation)) {
				addCitation(this.reply, annotation.url_citation.url, annotation.url_citation.title);
			}
		}
		for (const call of array(message.tool_calls)) {
			if (!isObject(call)) continue;
			const fn = isObject(call.function) ? call.function : {};
			if (!delta) {
				this.reply.toolCalls.push({ name: str(fn.name), arguments: str(fn.arguments) });
				continue;
			}
			const index = typeof call.index === 'number' ? call.index : this.chatTools.size;
			let tool = this.chatTools.get(index);
			if (!tool) {
				tool = { name: '', arguments: '' };
				this.chatTools.set(index, tool);
				this.reply.toolCalls.push(tool);
			}
			tool.name += str(fn.name);
			tool.arguments += str(fn.arguments);
		}
	}

	private chatChunk(data: Json) {
		for (const choice of array(data.choices)) {
			if (isObject(choice) && isObject(choice.delta)) this.chatMessage(choice.delta, true);
		}
	}

	private responsesItem(item: Json) {
		switch (item.type) {
			case 'message':
				for (const part of array(item.content)) {
					if (!isObject(part)) continue;
					if (part.type === 'output_text') this.reply.text += str(part.text);
					if (part.type === 'refusal') this.reply.text += str(part.refusal);
					for (const annotation of array(part.annotations)) {
						if (isObject(annotation) && annotation.type === 'url_citation') {
							addCitation(this.reply, annotation.url, annotation.title);
						}
					}
				}
				return;
			case 'reasoning':
				for (const part of array(item.summary)) if (isObject(part)) this.reply.reasoning += str(part.text);
				return;
			case 'function_call':
				this.reply.toolCalls.push({ name: str(item.name), arguments: str(item.arguments) });
				return;
			case 'image_generation_call':
				addImage(this.reply, imageFromBase64(item.result, `image/${str(item.output_format) || 'png'}`));
				return;
		}
	}

	private responsesEvent(data: Json) {
		switch (data.type) {
			case 'response.output_text.delta':
				this.reply.text += str(data.delta);
				return;
			case 'response.reasoning_summary_text.delta':
				this.reply.reasoning += str(data.delta);
				return;
			case 'response.output_text.annotation.added':
				if (isObject(data.annotation)) addCitation(this.reply, data.annotation.url, data.annotation.title);
				return;
			case 'response.output_item.done':
				// 文字和推理已经从 delta 拿到了；这里只补流里不给增量的东西。中途的预览图不显示（Q206）。
				if (!isObject(data.item)) return;
				if (data.item.type === 'function_call' || data.item.type === 'image_generation_call') {
					this.responsesItem(data.item);
				}
				if (data.item.type === 'message') {
					for (const part of array(data.item.content)) {
						if (!isObject(part)) continue;
						for (const annotation of array(part.annotations)) {
							if (isObject(annotation)) addCitation(this.reply, annotation.url, annotation.title);
						}
					}
				}
				return;
			case 'response.failed':
			case 'response.incomplete': {
				const response = isObject(data.response) ? data.response : {};
				const reason = isObject(response.error)
					? str(response.error.message)
					: isObject(response.incomplete_details)
						? '回复没写完：' + str(response.incomplete_details.reason)
						: '';
				if (reason) this.reply.error = reason;
				return;
			}
		}
	}

	private anthropicBlock(block: Json) {
		switch (block.type) {
			case 'text':
				this.reply.text += str(block.text);
				for (const citation of array(block.citations)) {
					if (isObject(citation)) addCitation(this.reply, citation.url, citation.title);
				}
				return;
			case 'thinking':
				this.reply.reasoning += str(block.thinking);
				return;
			case 'tool_use':
			case 'server_tool_use':
				this.reply.toolCalls.push({ name: str(block.name), arguments: JSON.stringify(block.input ?? {}) });
				return;
			case 'web_search_tool_result':
				for (const result of array(block.content)) {
					if (isObject(result)) addCitation(this.reply, result.url, result.title);
				}
				return;
		}
	}

	private anthropicEvent(data: Json) {
		const index = typeof data.index === 'number' ? data.index : -1;
		switch (data.type) {
			case 'content_block_start': {
				const block = isObject(data.content_block) ? data.content_block : {};
				const kind = str(block.type);
				if (kind === 'tool_use' || kind === 'server_tool_use') {
					const tool = { name: str(block.name), arguments: '' };
					this.reply.toolCalls.push(tool);
					this.anthropicBlocks.set(index, { kind, tool });
					return;
				}
				this.anthropicBlocks.set(index, { kind });
				// 搜索结果整块到达，不走 delta。
				if (kind === 'web_search_tool_result') this.anthropicBlock(block);
				return;
			}
			case 'content_block_delta': {
				const delta = isObject(data.delta) ? data.delta : {};
				switch (delta.type) {
					case 'text_delta':
						this.reply.text += str(delta.text);
						return;
					case 'thinking_delta':
						this.reply.reasoning += str(delta.thinking);
						return;
					case 'input_json_delta': {
						const tool = this.anthropicBlocks.get(index)?.tool;
						if (tool) tool.arguments += str(delta.partial_json);
						return;
					}
					case 'citations_delta':
						if (isObject(delta.citation)) addCitation(this.reply, delta.citation.url, delta.citation.title);
						return;
				}
				return;
			}
		}
	}
}

/** 从 OpenAI 或 Anthropic 样子的错误体里取那句话。 */
export function errorText(body: unknown): string | null {
	if (!isObject(body)) return null;
	if (isObject(body.error)) return str(body.error.message) || str(body.error.type) || null;
	if (typeof body.error === 'string') return body.error;
	return str(body.message) || str(body.title) || null;
}

// ——— 发送 ———

export interface SendResult {
	/** 网关给的请求 ID；网关在建出请求记录之前就拒绝了（比如 404 渠道）时为 null。 */
	requestId: string | null;
	status: number;
	reply: Reply;
	/** 客户端会收到的原文（流式时是全部 SSE 文本）。 */
	raw: string;
	stopped: boolean;
}

/**
 * 把请求体发到 /api/workbench 下的某个入口，边收边解析。
 * 用 fetch 而不是 api.ts：要读流、要能中途停（AbortSignal），而且上游的错误要原样显示，不能当成表单错误。
 */
export async function sendWorkbench(
	path: string,
	format: ApiFormat,
	body: string,
	signal: AbortSignal,
	onProgress: (reply: Reply) => void,
	kind: WorkbenchKind = 'chat'
): Promise<SendResult> {
	const parser = new ReplyParser(format, kind);
	let response: Response;
	try {
		response = await fetch(path, {
			method: 'POST',
			credentials: 'same-origin',
			headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
			body,
			signal
		});
	} catch (e) {
		const stopped = signal.aborted;
		parser.reply.error = stopped ? null : '连不上服务器，检查一下网络或服务是否在运行。';
		return { requestId: null, status: 0, reply: parser.reply, raw: '', stopped };
	}

	const requestId = response.headers.get('x-ntm-request-id');
	// 只有没进到中继的 401 才是登录失效；中继自己不会回 401（Q201）。
	if (response.status === 401 && !requestId) {
		const here = location.pathname + location.search;
		await goto(resolve('/login') + '?next=' + encodeURIComponent(here));
		throw new ApiError(401, { title: '需要重新登录。' });
	}

	let raw = '';
	let stopped = false;
	const isStream = (response.headers.get('content-type') ?? '').includes('text/event-stream');
	try {
		if (isStream && response.body) {
			const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
			let buffer = '';
			for (;;) {
				const { value, done } = await reader.read();
				if (done) break;
				raw += value;
				buffer += value;
				let cut: number;
				while ((cut = buffer.search(/\r?\n\r?\n/)) >= 0) {
					const frame = buffer.slice(0, cut);
					buffer = buffer.slice(cut).replace(/^\r?\n\r?\n/, '');
					const data = frame
						.split(/\r?\n/)
						.filter((line) => line.startsWith('data:'))
						.map((line) => line.slice(5).trimStart())
						.join('\n');
					if (!data || data === '[DONE]') continue;
					try {
						parser.event(JSON.parse(data) as Json);
					} catch {
						// 不是 JSON 的 data 不管它，原文里看得到。
					}
				}
				onProgress(parser.reply);
			}
		} else {
			raw = await response.text();
			try {
				const json = JSON.parse(raw) as unknown;
				if (response.ok && isObject(json)) parser.whole(json);
				else parser.reply.error = errorText(json) ?? `HTTP ${response.status}`;
			} catch {
				if (!response.ok) parser.reply.error = raw.slice(0, 500) || `HTTP ${response.status}`;
			}
		}
	} catch {
		stopped = signal.aborted;
		if (!stopped) parser.reply.error = parser.reply.error ?? '连接中途断了。';
	}

	if (!response.ok && !parser.reply.error) parser.reply.error = `HTTP ${response.status}`;
	onProgress(parser.reply);
	return { requestId, status: response.status, reply: parser.reply, raw, stopped };
}

/**
 * 请求记录是异步落库的（Q215）：回复结束后过一会儿才查得到，查几次。
 * 还在进行中的记录（status running）也再等等。
 */
export async function fetchRecord(requestId: string): Promise<RequestDetailView | null> {
	for (let i = 0; i < 8; i++) {
		try {
			const detail = await api.get<RequestDetailView>('/api/requests/' + requestId);
			if (detail.status !== 'running') return detail;
		} catch (e) {
			if (!(e instanceof ApiError) || e.status !== 404) return null;
		}
		await new Promise((r) => setTimeout(r, 250 + i * 250));
	}
	return null;
}

/** 选文件或粘贴得到的图片 → 附件。超过上限返回错误说明。 */
export async function readImage(file: File): Promise<ImageAttachment | string> {
	if (!file.type.startsWith('image/')) return `「${file.name}」不是图片。`;
	if (file.size > maxImageBytes) return `「${file.name}」超过 5 MB。`;
	const url = await new Promise<string>((done, fail) => {
		const reader = new FileReader();
		reader.onload = () => done(String(reader.result));
		reader.onerror = () => fail(reader.error);
		reader.readAsDataURL(file);
	});
	return { mediaType: file.type, data: url.slice(url.indexOf(',') + 1), name: file.name || '粘贴的图片' };
}

// ——— 设置（Q214）：只存设置，不存对话和图片。 ———

export interface WorkbenchSettings {
	channelPrompt: string;
	modelPrompt: string;
	/** 渠道测试发什么类型的请求（Q238）。 */
	channelKind: WorkbenchKind;
	embeddingInput: string;
	imagePrompt: string;
	imageSize: string;
	modelFormat: ApiFormat;
	effort: Effort;
	search: boolean;
	imageGeneration: boolean;
	stream: boolean;
	/** 每个渠道上次用的上游模型，键是渠道 id（Q265）。 */
	lastModels: Record<string, string>;
}

const settingsKey = 'ntm.workbench.settings';

export const defaultSettings: WorkbenchSettings = {
	channelPrompt: '1+1=？只回答结果。',
	modelPrompt: '1+1=？只回答结果。',
	channelKind: 'chat',
	embeddingInput: '南天门网关的嵌入测试。',
	imagePrompt: 'A red apple on a white table, simple flat illustration.',
	imageSize: '1024x1024',
	modelFormat: 'openAiChat',
	effort: 'off',
	search: false,
	imageGeneration: false,
	stream: true,
	lastModels: {}
};

export function loadSettings(): WorkbenchSettings {
	try {
		const stored = JSON.parse(localStorage.getItem(settingsKey) ?? '{}') as Partial<WorkbenchSettings>;
		return { ...defaultSettings, ...stored, lastModels: { ...(stored.lastModels ?? {}) } };
	} catch {
		return { ...defaultSettings };
	}
}

export function saveSettings(settings: WorkbenchSettings) {
	try {
		localStorage.setItem(settingsKey, JSON.stringify(settings));
	} catch {
		// 配额满了或被禁用：设置不保存，不影响使用。
	}
}
