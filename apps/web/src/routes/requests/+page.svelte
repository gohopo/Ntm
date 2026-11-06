<script lang="ts">
	import { onMount } from 'svelte';
	import { replaceState } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import {
		failureLabels,
		formatCost,
		formatCount,
		formatElapsed,
		formatMs,
		formatTime,
		publishableKinds,
		requestKindLabels,
		statusLabels
	} from '#lib/format.ts';
	import { currentAttempt, LiveFeed } from '#lib/live.svelte.ts';
	import type {
		LiveEvent,
		LiveRequest,
		RequestKind,
		RequestOrigin,
		RequestPageView,
		RequestRowView,
		RequestStatus,
		ApiKeyView,
		ChannelView
	} from '#lib/types.ts';

	const statuses = Object.keys(statusLabels) as RequestStatus[];

	let model = $state('');
	let status = $state<RequestStatus | ''>('');
	let apiKeyId = $state<number | ''>('');
	let origin = $state<RequestOrigin | ''>('');
	let kind = $state<RequestKind | ''>('');
	let channelId = $state<number | ''>('');
	let apiKeys = $state<ApiKeyView[]>([]);
	let channels = $state<ChannelView[]>([]);

	let rows = $state<RequestRowView[]>([]);
	let nextCursor = $state<string | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);

	// 进行中的请求走实时链路（Q111）；结束的请求不插进下面的分页表，只提示有几条新的。
	let feed = $state<LiveFeed | null>(null);
	let now = $state(Date.now());
	let finishedSinceLoad = $state(0);
	let missedWhileAway = $state(false);
	let snapshots = 0;
	let interruptError = $state<string | null>(null);

	function onLiveEvent(event: LiveEvent) {
		if (event.type === 'requestFinished') finishedSinceLoad += 1;
		// 第二份及以后的快照说明断过线，断线期间结束的请求数不清了。
		if (event.type === 'snapshot' && ++snapshots > 1) missedWhileAway = true;
	}

	async function interrupt(request: LiveRequest) {
		if (!confirm(`中断这个「${request.publishedModel}」请求？客户端会收到错误，不会换渠道重试。`)) return;
		interruptError = null;
		try {
			await api.post('/api/requests/' + request.requestId + '/interrupt');
			feed?.markInterrupted(request.requestId);
		} catch (e) {
			interruptError = e instanceof ApiError && e.status === 404 ? '这个请求已经结束了。' : messageOf(e);
		}
	}

	/** 当前筛选条件，同时用于查接口和写进地址栏（Q264）。 */
	function filters(): URLSearchParams {
		const params = new URLSearchParams();
		if (model.trim()) params.set('model', model.trim());
		if (status) params.set('status', status);
		if (apiKeyId !== '') params.set('apiKeyId', String(apiKeyId));
		if (origin) params.set('origin', origin);
		if (kind) params.set('kind', kind);
		if (channelId !== '') params.set('channelId', String(channelId));
		return params;
	}

	/** 打开带参数的地址时直接按参数筛好；认不出的值当没给。 */
	function readFilters(params: Pick<URLSearchParams, 'get' | 'has'>) {
		model = params.get('model') ?? '';
		const s = params.get('status');
		status = s && s in statusLabels ? (s as RequestStatus) : '';
		const key = Number(params.get('apiKeyId'));
		apiKeyId = params.has('apiKeyId') && Number.isInteger(key) ? key : '';
		const o = params.get('origin');
		origin = o === 'client' || o === 'workbench' ? o : '';
		const k = params.get('kind');
		kind = k && (publishableKinds as string[]).includes(k) ? (k as RequestKind) : '';
		const channel = Number(params.get('channelId'));
		channelId = params.has('channelId') && Number.isInteger(channel) ? channel : '';
	}

	function query(cursor: string | null): string {
		const params = filters();
		params.set('limit', '50');
		if (cursor) params.set('cursor', cursor);
		return '/api/requests?' + params.toString();
	}

	/** 改筛选只替换当前这条历史记录，不新增（Q264）。 */
	function writeFilters() {
		const search = filters().toString();
		const target = resolve('/requests') + (search ? '?' + search : '');
		if (target !== page.url.pathname + page.url.search) replaceState(target, {});
	}

	async function load(more = false) {
		loading = true;
		error = null;
		if (!more) {
			finishedSinceLoad = 0;
			missedWhileAway = false;
			writeFilters();
		}
		try {
			const page = await api.get<RequestPageView>(query(more ? nextCursor : null));
			rows = more ? [...rows, ...page.items] : page.items;
			nextCursor = page.nextCursor;
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	function submit(event: SubmitEvent) {
		event.preventDefault();
		void load();
	}

	function reset() {
		model = '';
		status = '';
		apiKeyId = '';
		origin = '';
		kind = '';
		channelId = '';
		void load();
	}

	// onMount 而不是 $effect：load() 会读筛选条件，放进 effect 会变成每敲一个字就重新查一次。
	onMount(() => {
		readFilters(page.url.searchParams);
		void load();
		api.get<ApiKeyView[]>('/api/api-keys').then(
			(list) => (apiKeys = list),
			() => (apiKeys = [])
		);
		api.get<ChannelView[]>('/api/channels').then(
			(list) => (channels = list),
			() => (channels = [])
		);

		const live = new LiveFeed(onLiveEvent);
		feed = live;
		const tick = setInterval(() => (now = Date.now()), 1000);
		return () => {
			clearInterval(tick);
			live.close();
		};
	});

	function statusClass(row: RequestRowView): string {
		return row.status === 'succeeded' ? 'ok' : row.status === 'running' ? 'muted' : 'error';
	}
</script>

<svelte:head>
	<title>请求日志 · 南天门</title>
</svelte:head>

<h1>请求日志</h1>

<section class="panel" aria-labelledby="in-flight">
	<h2 id="in-flight">
		进行中（{feed?.requests.size ?? 0}）
		{#if feed && !feed.connected}<span class="badge warn">实时连接已断开，正在重连</span>{/if}
	</h2>
	{#if interruptError}
		<p class="alert" role="alert">{interruptError}</p>
	{/if}
	{#if !feed || feed.requests.size === 0}
		<p class="muted">现在没有进行中的请求。</p>
	{:else}
		<div class="scroll">
			<table>
				<caption class="visually-hidden">进行中的请求，最早开始的在前</caption>
				<thead>
					<tr>
						<th scope="col" class="num">已进行</th>
						<th scope="col">下发模型</th>
						<th scope="col">API 密钥</th>
						<th scope="col">当前渠道 / 渠道密钥</th>
						<th scope="col">上游模型</th>
						<th scope="col" class="num">尝试</th>
						<th scope="col">阶段</th>
						<th scope="col"><span class="visually-hidden">操作</span></th>
					</tr>
				</thead>
				<tbody>
					{#each feed.list as request (request.requestId)}
						{@const attempt = currentAttempt(request)}
						<tr>
							<td class="num">
								<a href={resolve('/requests/[id]', { id: request.requestId })}>
									{formatElapsed(request.startedAt, now)}
								</a>
							</td>
							<td class="mono">
								{request.publishedModel}
								{#if request.requestKind !== 'chat'}<span class="badge">{requestKindLabels[request.requestKind]}</span>{/if}
							</td>
							<td>
								{#if request.origin === 'workbench'}<span class="badge">工作台</span>{:else}{request.apiKeyName ?? '—'}{/if}
							</td>
							<td>
								{#if attempt}
									{attempt.channelName}<br /><span class="mono muted">{attempt.keyPrefix}…</span>
								{:else}
									<span class="muted">—</span>
								{/if}
							</td>
							<td class="mono">{attempt?.upstreamModel ?? '—'}</td>
							<td class="num">{request.attempts.length}</td>
							<td>
								{#if request.interruptRequested}
									<span class="badge warn">正在中断</span>
								{:else if request.committedAt}
									<span class="ok">已提交</span>{#if request.isStream}<span class="muted">，流式输出中</span>{/if}
								{:else if attempt?.finishedAt}
									<span class="error">{attempt.failureClass ? failureLabels[attempt.failureClass] : '—'}</span>
									<span class="muted">，换下一个</span>
								{:else if attempt}
									<span class="muted">等上游</span>
								{:else}
									<span class="muted">选路中</span>
								{/if}
							</td>
							<td>
								<button
									type="button"
									class="danger"
									onclick={() => interrupt(request)}
									disabled={request.interruptRequested}>中断</button
								>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</section>

<h2>已结束</h2>

<form class="toolbar" onsubmit={submit} aria-label="筛选请求">
	<label class="field">
		<span>下发模型</span>
		<input type="search" bind:value={model} placeholder="完整的模型名" />
	</label>
	<label class="field">
		<span>状态</span>
		<select bind:value={status}>
			<option value="">全部</option>
			{#each statuses as s (s)}
				<option value={s}>{statusLabels[s]}</option>
			{/each}
		</select>
	</label>
	<label class="field">
		<span>API 密钥</span>
		<select bind:value={apiKeyId}>
			<option value="">全部</option>
			{#each apiKeys as apiKey (apiKey.id)}
				<option value={apiKey.id}>{apiKey.name}</option>
			{/each}
		</select>
	</label>
	<label class="field">
		<span>渠道</span>
		<select bind:value={channelId}>
			<option value="">全部</option>
			{#each channels as channel (channel.id)}
				<option value={channel.id}>{channel.name}</option>
			{/each}
			{#if channelId !== '' && !channels.some((c) => c.id === channelId)}
				<option value={channelId}>渠道 #{channelId}</option>
			{/if}
		</select>
	</label>
	<label class="field">
		<span>来源</span>
		<select bind:value={origin}>
			<option value="">全部</option>
			<option value="client">客户端</option>
			<option value="workbench">工作台</option>
		</select>
	</label>
	<label class="field">
		<span>类型</span>
		<select bind:value={kind}>
			<option value="">全部</option>
			{#each publishableKinds as k (k)}
				<option value={k}>{requestKindLabels[k]}</option>
			{/each}
		</select>
	</label>
	<button class="primary" type="submit" disabled={loading}>筛选</button>
	<button type="button" onclick={reset} disabled={loading}>清空并刷新</button>
</form>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

{#if finishedSinceLoad > 0 || missedWhileAway}
	<p>
		<button type="button" onclick={() => load()} disabled={loading}>
			{finishedSinceLoad > 0 ? `有 ${finishedSinceLoad} 条新记录` : '断线期间可能有新记录'}{missedWhileAway && finishedSinceLoad > 0
				? '（断线期间的没算进去）'
				: ''}，点击刷新
		</button>
	</p>
{/if}

<div class="scroll">
	<table>
		<caption class="visually-hidden">请求记录，最新的在前</caption>
		<thead>
			<tr>
				<th scope="col">开始时间</th>
				<th scope="col">下发模型</th>
				<th scope="col">渠道 / 上游模型</th>
				<th scope="col">API 密钥</th>
				<th scope="col">状态</th>
				<th scope="col" class="num">尝试</th>
				<th scope="col" class="num">首字</th>
				<th scope="col" class="num">耗时</th>
				<th scope="col" class="num">输入</th>
				<th scope="col" class="num">缓存读 / 写</th>
				<th scope="col" class="num">输出</th>
				<th scope="col" class="num">费用</th>
			</tr>
		</thead>
		<tbody>
			{#each rows as row (row.id)}
				<tr>
					<td>
						<a href={resolve('/requests/[id]', { id: row.id })}>{formatTime(row.startedAt)}</a>
					</td>
					<td class="mono">
						{row.publishedModel}
						{#if row.requestKind !== 'chat'}<span class="badge">{requestKindLabels[row.requestKind]}</span>{/if}
					</td>
					<td>
						{row.channelName ?? '—'}
						{#if row.upstreamModel}<br /><span class="mono muted">{row.upstreamModel}</span>{/if}
					</td>
					<td>
						{#if row.origin === 'workbench'}<span class="badge">工作台</span>{:else}{row.apiKeyName ?? '—'}{/if}
					</td>
					<td>
						<span class={statusClass(row)}>{statusLabels[row.status]}</span>
						{#if row.blockedBy}
							<span class="muted">（{failureLabels[row.blockedBy]}）</span>
						{/if}
						{#if row.httpStatusCode}<span class="muted"> {row.httpStatusCode}</span>{/if}
						{#if row.isStream}<span class="badge muted">流式</span>{/if}
					</td>
					<td class="num">{row.attemptCount}</td>
					<td class="num">{formatMs(row.timeToFirstContentMs)}</td>
					<td class="num">{formatMs(row.durationMs)}</td>
					<td class="num">{formatCount(row.inputTokens)}</td>
					<td class="num">{formatCount(row.cacheReadTokens)} / {formatCount(row.cacheWriteTokens)}</td>
					<td class="num">
						{formatCount(row.outputTokens)}
						{#if row.usageSource === 'gatewayEstimated'}<span class="muted" title="网关估算">≈</span>{/if}
						{#if row.imageCount > 0}<br /><span class="muted" style="white-space: nowrap">{row.imageCount} 张图</span>{/if}
					</td>
					<td class="num">
						{#if row.unpriced}
							<span
								class="badge"
								title={row.imageCount > 0
									? '生成了图片，但这个上游模型没有每张图片的单价，也没有 token 单价或用量'
									: '用了 Token，但这个上游模型没有单价'}>无单价</span
							>
						{:else}
							{formatCost(row.costNanoUsd)}
							{#if row.usageSource === 'gatewayEstimated' && row.costNanoUsd !== null}<span
									class="badge"
									title="上游没给用量，按字符估算">估</span
								>{/if}
						{/if}
					</td>
				</tr>
			{:else}
				<tr>
					<td colspan="12" class="muted">{loading ? '加载中…' : '没有符合条件的请求。'}</td>
				</tr>
			{/each}
		</tbody>
	</table>
</div>

{#if nextCursor}
	<div class="actions">
		<button type="button" onclick={() => load(true)} disabled={loading}>
			{loading ? '加载中…' : '加载更多'}
		</button>
	</div>
{/if}

<style>
	.scroll {
		overflow-x: auto;
	}

	h2 .badge {
		margin-left: 0.5rem;
		font-size: 0.8rem;
		font-weight: normal;
	}
</style>
