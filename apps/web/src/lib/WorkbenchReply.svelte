<script lang="ts">
	// Workbench 的一条回复（Q215）：正文纯文本、推理可折叠、图片直接显示；
	// 下面一行是从请求记录里取的落点、时间、用量和费用；「原始」里是请求体和响应原文。
	import { resolve } from '$app/paths';
	import { failureLabels, formatCost, formatCount, formatMs } from './format.ts';
	import type { RequestDetailView } from './types.ts';
	import type { Reply } from './workbench.ts';

	interface Props {
		reply: Reply;
		running?: boolean;
		requestId?: string | null;
		record?: RequestDetailView | null;
		/** 记录还在查。 */
		loadingRecord?: boolean;
		requestBody?: string;
		raw?: string;
		stopped?: boolean;
	}

	let {
		reply,
		running = false,
		requestId = null,
		record = null,
		loadingRecord = false,
		requestBody = '',
		raw = '',
		stopped = false
	}: Props = $props();

	const last = $derived(record?.attempts.at(-1) ?? null);
	const failures = $derived(record?.attempts.filter((a) => a.failureClass !== 'none') ?? []);

	function prettyBody(text: string): string {
		try {
			return JSON.stringify(JSON.parse(text), null, 2);
		} catch {
			return text;
		}
	}
</script>

<div class="reply">
	{#if reply.reasoning}
		<details class="reasoning" open={running && !reply.text}>
			<summary>推理（{reply.reasoning.length} 字）</summary>
			<p class="text muted">{reply.reasoning}</p>
		</details>
	{/if}

	{#if reply.embedding}
		<p>
			{reply.embedding.count} 条向量，每条 {reply.embedding.dimensions} 维{reply.embedding.base64 ? '（base64，已解码）' : ''}。
			{#if reply.embedding.head.length > 0}
				前 {reply.embedding.head.length} 个值：<span class="mono"
					>{reply.embedding.head.map((v) => v.toFixed(6)).join(', ')}</span
				>
			{/if}
		</p>
	{/if}

	{#if reply.text}
		<p class="text">{reply.text}</p>
	{:else if running && !reply.embedding && reply.images.length === 0}
		<p class="muted">等回复…</p>
	{/if}

	{#each reply.images as src, i (src)}
		<figure>
			<img {src} alt={`生成的第 ${i + 1} 张图片`} />
			<figcaption><a href={src} download={`image-${i + 1}.png`}>下载</a></figcaption>
		</figure>
	{/each}

	{#if reply.toolCalls.length > 0}
		<ul class="tools">
			{#each reply.toolCalls as tool, i (i)}
				<li><span class="badge">工具</span> <span class="mono">{tool.name}</span> <code>{tool.arguments}</code></li>
			{/each}
		</ul>
	{/if}

	{#if reply.citations.length > 0}
		<ol class="citations">
			{#each reply.citations as citation (citation.url)}
				<li><a href={citation.url} target="_blank" rel="noopener noreferrer">{citation.title}</a></li>
			{/each}
		</ol>
	{/if}

	{#if stopped}
		<p class="muted">已停止。已经产生的用量照常计费。</p>
	{/if}
	{#if reply.error}
		<p class="alert" role="alert">{reply.error}</p>
	{/if}
	{#each failures as attempt (attempt.ordinal)}
		<p class="error small">
			第 {attempt.ordinal} 次 · {attempt.channelName} / <span class="mono">{attempt.upstreamModel}</span> ·
			{failureLabels[attempt.failureClass]}{#if attempt.httpStatusCode}（HTTP {attempt.httpStatusCode}）{/if}{#if attempt.errorMessage}：{attempt.errorMessage}{/if}
		</p>
	{/each}

	{#if !running && (requestId || requestBody)}
		<p class="meta muted">
			{#if last}
				{last.channelName} / <span class="mono">{last.upstreamModel}</span> /
				<span class="mono">{last.channelKeyPrefix}…</span>
				{#if !last.passthrough}<span class="badge">已转换</span>{/if}
				· 首字 {formatMs(record?.timeToFirstContentMs)} · 耗时 {formatMs(record?.durationMs)}
				· 输入 {formatCount(record?.inputTokens)} · 缓存读 {formatCount(record?.cacheReadTokens)}
				· 缓存写 {formatCount((record?.cacheWrite5mTokens ?? 0) + (record?.cacheWrite1hTokens ?? 0))}
				· 输出 {formatCount(record?.outputTokens)}
				{#if record?.requestKind === 'image'}· 图片 {formatCount(record?.imageCount)} 张{/if}
				· 费用 {record?.unpriced ? '无单价' : formatCost(record?.costNanoUsd)}
				· 尝试 {record?.attempts.length}
				{#if last.responseId}· 响应 id <span class="mono">{last.responseId}</span>{/if}
			{:else if loadingRecord}
				正在取请求记录…
			{:else if requestId && !record}
				请求记录还没写好，稍后在请求日志里看。
			{:else if record}
				没有发到任何上游{#if record.blockDetail}：{record.blockDetail}{/if}
			{/if}
			{#if requestId}
				· <a href={resolve('/requests/[id]', { id: requestId })}>请求详情和调试原文</a>
			{/if}
		</p>
		<details class="raw">
			<summary>原始</summary>
			<h4>请求体</h4>
			<pre>{prettyBody(requestBody)}</pre>
			<h4>响应</h4>
			<pre>{raw.length > 200_000 ? raw.slice(0, 200_000) + '\n…（太长，截断了）' : raw || '（空）'}</pre>
		</details>
	{/if}
</div>

<style>
	.reply {
		display: grid;
		gap: 0.5rem;
	}
	.text {
		white-space: pre-wrap;
		overflow-wrap: anywhere;
		margin: 0;
	}
	.reasoning summary,
	.raw summary {
		cursor: pointer;
		color: var(--muted);
	}
	img {
		max-width: min(100%, 32rem);
		border: 1px solid var(--border);
		border-radius: 4px;
	}
	figure {
		margin: 0;
	}
	.tools,
	.citations {
		margin: 0;
		padding-left: 1.25rem;
	}
	code {
		overflow-wrap: anywhere;
	}
	.meta,
	.small {
		font-size: 0.85rem;
		margin: 0;
	}
	pre {
		max-height: 24rem;
		overflow: auto;
		white-space: pre-wrap;
		overflow-wrap: anywhere;
		font-size: 0.8rem;
		background: var(--bg);
		padding: 0.5rem;
		border-radius: 4px;
	}
	h4 {
		margin: 0.5rem 0 0.25rem;
		font-size: 0.85rem;
	}
</style>
