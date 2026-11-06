import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import type { Problem } from './types';

/** 后端回了非 2xx。title 是给人看的那句话，fields 是按字段（camelCase，如 rules[0].pattern）分好的校验错误。 */
export class ApiError extends Error {
	readonly status: number;
	readonly fields: Record<string, string[]>;

	constructor(status: number, problem: Problem) {
		super(problem.title ?? problem.detail ?? `请求失败（HTTP ${status}）。`);
		this.name = 'ApiError';
		this.status = status;
		this.fields = problem.errors ?? {};
	}

	/** 某个字段的第一条错误，没有就是 undefined。 */
	field(name: string): string | undefined {
		return this.fields[name]?.[0];
	}

	/** 界面上没有对应输入框的那些字段错误，拼成一句话。 */
	summary(shown: string[] = []): string {
		const rest = Object.entries(this.fields)
			.filter(([key]) => !shown.includes(key))
			.flatMap(([, messages]) => messages);
		return rest.length > 0 ? rest.join(' ') : this.message;
	}
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

async function send<T>(method: Method, path: string, body?: unknown): Promise<T> {
	const init: RequestInit = {
		method,
		credentials: 'same-origin',
		headers: { Accept: 'application/json' }
	};
	if (body !== undefined) {
		init.headers = { ...init.headers, 'Content-Type': 'application/json' };
		init.body = JSON.stringify(body);
	}

	let response: Response;
	try {
		response = await fetch(path, init);
	} catch {
		throw new ApiError(0, { title: '连不上服务器，检查一下网络或服务是否在运行。' });
	}

	if (response.status === 401) {
		// 会话没了（过期、改过密码、被重置）：回登录页，登完再回来。
		const here = location.pathname + location.search;
		if (location.pathname !== resolve('/login')) {
			await goto(resolve('/login') + '?next=' + encodeURIComponent(here));
		}
		throw new ApiError(401, { title: '需要重新登录。' });
	}

	if (!response.ok) {
		let problem: Problem = {};
		try {
			problem = (await response.json()) as Problem;
		} catch {
			// 不是 JSON（比如反向代理的错误页），用通用说法。
		}
		if (response.status === 429 && !problem.title) {
			problem.title = '操作太频繁了，稍等一会儿再试。';
		}
		throw new ApiError(response.status, problem);
	}

	if (response.status === 204) {
		return undefined as T;
	}
	return (await response.json()) as T;
}

export const api = {
	get: <T>(path: string) => send<T>('GET', path),
	post: <T>(path: string, body?: unknown) => send<T>('POST', path, body ?? {}),
	put: <T>(path: string, body: unknown) => send<T>('PUT', path, body),
	patch: (path: string, body: unknown) => send<void>('PATCH', path, body),
	delete: (path: string) => send<void>('DELETE', path)
};

/** 把任意异常变成一句能显示的话。 */
export function messageOf(error: unknown): string {
	if (error instanceof ApiError) {
		return error.summary();
	}
	return error instanceof Error ? error.message : String(error);
}
