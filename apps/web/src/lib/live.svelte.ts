import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { SvelteMap } from 'svelte/reactivity';
import type { LiveAttempt, LiveEvent, LiveRequest } from './types';

/**
 * 订阅 /api/live（Q110）：连上时先拿到全部进行中请求的快照，之后跟着状态变化增量更新。
 * 断线重连就是重新拿一份快照，没有补发。页面不可见时断开，省下服务器那 20 个名额，回来时重连。
 * 在 onMount 里创建，把 close 当清理函数。
 */
export class LiveFeed {
	/** 进行中的请求，按 requestId。 */
	readonly requests = new SvelteMap<string, LiveRequest>();
	connected = $state(false);

	#source: EventSource | null = null;
	#retry: ReturnType<typeof setTimeout> | undefined;
	#closed = false;
	readonly #onEvent: ((event: LiveEvent) => void) | undefined;

	constructor(onEvent?: (event: LiveEvent) => void) {
		this.#onEvent = onEvent;
		document.addEventListener('visibilitychange', this.#onVisibility);
		if (document.visibilityState === 'visible') this.#open();
	}

	/** 按开始时间排好的进行中请求，最早的在前。 */
	get list(): LiveRequest[] {
		return [...this.requests.values()].sort((a, b) => a.startedAt.localeCompare(b.startedAt));
	}

	/** 中断请求发出去之后先在本地标上，实时链路不单独推这个变化。 */
	markInterrupted(requestId: string) {
		const request = this.requests.get(requestId);
		if (request) this.requests.set(requestId, { ...request, interruptRequested: true });
	}

	close = () => {
		this.#closed = true;
		document.removeEventListener('visibilitychange', this.#onVisibility);
		this.#disconnect();
	};

	#onVisibility = () => {
		if (this.#closed) return;
		if (document.visibilityState === 'visible') {
			if (!this.#source) this.#open();
		} else {
			this.#disconnect();
		}
	};

	#open() {
		clearTimeout(this.#retry);
		const source = new EventSource('/api/live');
		this.#source = source;
		source.onopen = () => (this.connected = true);
		source.onmessage = (message: MessageEvent<string>) => this.#apply(JSON.parse(message.data) as LiveEvent);
		source.onerror = () => {
			this.connected = false;
			// 服务器按时关掉连接时浏览器会自己重连（readyState 是 CONNECTING）。
			// 回了非 200（会话过期 401、连接数满 429）时浏览器不再重试，readyState 是 CLOSED，由这里接手。
			if (source.readyState === EventSource.CLOSED && this.#source === source) {
				this.#source = null;
				void this.#recover();
			}
		};
	}

	#disconnect() {
		clearTimeout(this.#retry);
		this.#source?.close();
		this.#source = null;
		this.connected = false;
	}

	async #recover() {
		const me = await fetch('/api/auth/me', { credentials: 'same-origin' }).catch(() => null);
		if (this.#closed) return;
		if (me?.status === 401) {
			const here = location.pathname + location.search;
			await goto(resolve('/login') + '?next=' + encodeURIComponent(here));
			return;
		}
		this.#retry = setTimeout(() => {
			if (!this.#closed && document.visibilityState === 'visible') this.#open();
		}, 5000);
	}

	#apply(event: LiveEvent) {
		switch (event.type) {
			case 'snapshot':
				this.requests.clear();
				for (const request of event.requests) this.requests.set(request.requestId, request);
				break;
			case 'requestStarted':
				this.requests.set(event.request.requestId, event.request);
				break;
			case 'attemptStarted':
			case 'attemptFinished':
				this.#update(event.requestId, (r) => ({ ...r, attempts: upsert(r.attempts, event.attempt) }));
				break;
			case 'committed':
				this.#update(event.requestId, (r) => ({ ...r, committedAt: event.committedAt }));
				break;
			case 'requestFinished':
				this.requests.delete(event.requestId);
				break;
		}
		this.#onEvent?.(event);
	}

	#update(requestId: string, change: (request: LiveRequest) => LiveRequest) {
		const request = this.requests.get(requestId);
		if (request) this.requests.set(requestId, change(request));
	}
}

function upsert(attempts: LiveAttempt[], attempt: LiveAttempt): LiveAttempt[] {
	const rest = attempts.filter((a) => a.attemptNumber !== attempt.attemptNumber);
	return [...rest, attempt].sort((a, b) => a.attemptNumber - b.attemptNumber);
}

/** 进行中请求当前（最后一次）的尝试。 */
export function currentAttempt(request: LiveRequest): LiveAttempt | undefined {
	return request.attempts.at(-1);
}
