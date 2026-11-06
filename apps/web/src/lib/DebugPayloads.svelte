<script lang="ts">
	import { api, messageOf } from '#lib/api.ts';
	import type { DebugPayloadKind, DebugPayloadView } from '#lib/types.ts';

	// 一个 Request 的调试原文（Q162）。没有原文就什么都不显示，免得每个请求都挂一块空壳。
	let { requestId }: { requestId: string } = $props();

	let payloads = $state<DebugPayloadView[] | null>(null);
	let error = $state<string | null>(null);
	let open = $state(false);
	let copied = $state<string | null>(null);

	const kindLabels: Record<DebugPayloadKind, string> = {
		clientRequest: '客户端请求',
		upstreamRequest: '发给上游的请求',
		upstreamResponse: '上游响应'
	};

	const attempts = $derived.by(() => {
		const groups = new Map<number, DebugPayloadView[]>();
		for (const payload of payloads ?? []) {
			const list = groups.get(payload.attemptOrdinal) ?? [];
			list.push(payload);
			groups.set(payload.attemptOrdinal, list);
		}
		return [...groups.entries()].sort((a, b) => a[0] - b[0]);
	});

	async function load() {
		try {
			payloads = await api.get<DebugPayloadView[]>(`/api/requests/${requestId}/debug`);
		} catch (e) {
			error = messageOf(e);
		}
	}

	async function copy(payload: DebugPayloadView, id: string) {
		const text = payload.headers + '\n' + payload.body;
		try {
			await navigator.clipboard.writeText(text);
			copied = id;
			setTimeout(() => (copied = null), 2000);
		} catch (e) {
			error = messageOf(e);
		}
	}

	function size(bytes: number) {
		return bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
	}

	void load();
</script>

{#if error}
	<p class="alert" role="alert">{error}</p>
{:else if payloads && payloads.length > 0}
	<h2>调试原文（{payloads.length}）</h2>
	<div class="actions">
		<button type="button" onclick={() => (open = !open)}>{open ? '收起' : '展开'}</button>
		<a class="download" href={`/api/requests/${requestId}/debug?download=true`} download>
			下载这个请求的全部原文
		</a>
	</div>

	{#if open}
		{#each attempts as [ordinal, items] (ordinal)}
			<h3>尝试 {ordinal}</h3>
			{#each items as payload (payload.kind)}
				{@const id = `${ordinal}-${payload.kind}`}
				<div class="payload">
					<div class="head">
						<strong>{kindLabels[payload.kind]}</strong>
						{#if payload.httpStatusCode}<span class="muted">HTTP {payload.httpStatusCode}</span>{/if}
						<span class="muted">{size(payload.byteCount)}</span>
						{#if payload.truncated}<span class="badge warn">已截断</span>{/if}
						<button type="button" onclick={() => copy(payload, id)}>
							{copied === id ? '已复制' : '复制'}
						</button>
					</div>
					<pre>{payload.headers}</pre>
					<pre class="body">{payload.body}</pre>
				</div>
			{/each}
		{/each}
	{/if}
{/if}

<style>
	.actions {
		display: flex;
		align-items: center;
		gap: 0.75rem;
		margin-bottom: 0.5rem;
	}

	.payload {
		margin-bottom: 1rem;
	}

	.head {
		display: flex;
		align-items: center;
		gap: 0.5rem;
		margin-bottom: 0.25rem;
	}

	pre {
		margin: 0 0 0.25rem;
		padding: 0.5rem;
		border: 1px solid var(--border);
		border-radius: 0.25rem;
		background: var(--panel);
		overflow-x: auto;
		white-space: pre-wrap;
		word-break: break-all;
		font-size: 0.8rem;
		max-height: 20rem;
	}

	.body {
		max-height: 30rem;
	}
</style>
