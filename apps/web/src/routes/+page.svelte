<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api, messageOf } from '#lib/api.ts';
	import { LiveFeed } from '#lib/live.svelte.ts';
	import BarChart from '#lib/BarChart.svelte';
	import { formatCost, formatCount, formatMs, formatTime } from '#lib/format.ts';
	import type {
		ModelSyncEventListView,
		StatsGroupView,
		StatsRange,
		StatsTotalsView,
		StatsView
	} from '#lib/types.ts';

	const ranges: { value: StatsRange; label: string }[] = [
		{ value: '24h', label: '24 小时' },
		{ value: '7d', label: '7 天' },
		{ value: '30d', label: '30 天' },
		{ value: '90d', label: '90 天' }
	];

	type Tab = 'publishedModels' | 'channels' | 'upstreamModels' | 'apiKeys';
	const tabs: { value: Tab; label: string }[] = [
		{ value: 'publishedModels', label: '下发模型' },
		{ value: 'channels', label: '渠道' },
		{ value: 'upstreamModels', label: '上游模型' },
		{ value: 'apiKeys', label: 'API 密钥' }
	];

	type SortKey = 'cost' | 'requests' | 'name';

	/** 页面可见时每 60 秒自动刷新一次，看不见就停（Q64）。 */
	const refreshMs = 60_000;

	const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';

	let range = $state<StatsRange>('24h');
	let tab = $state<Tab>('publishedModels');
	let sort = $state<SortKey>('cost');
	let stats = $state<StatsView | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);
	let loadedAt = $state<Date | null>(null);

	let sequence = 0;

	async function load() {
		const mine = ++sequence;
		loading = true;
		error = null;
		try {
			const params = new URLSearchParams({ range, tz: timeZone });
			const result = await api.get<StatsView>('/api/stats?' + params.toString());
			if (mine === sequence) {
				stats = result;
				loadedAt = new Date();
			}
		} catch (e) {
			if (mine === sequence) error = messageOf(e);
		} finally {
			if (mine === sequence) loading = false;
		}
	}

	function choose(next: StatsRange) {
		if (next === range) return;
		range = next;
		void load();
	}

	// 新模型进入选路的提示（Q140）。只在有未确认的「进入选路」时出现。
	let syncEvents = $state<ModelSyncEventListView | null>(null);
	let acknowledging = $state(false);

	async function loadSyncEvents() {
		try {
			syncEvents = await api.get<ModelSyncEventListView>('/api/model-sync-events');
		} catch {
			// 提示性信息，取不到就不显示。
		}
	}

	async function acknowledgeSync() {
		acknowledging = true;
		try {
			await api.post('/api/model-sync-events/acknowledge', {});
			await loadSyncEvents();
		} catch (e) {
			error = messageOf(e);
		} finally {
			acknowledging = false;
		}
	}

	onMount(loadSyncEvents);

	// 「进行中」不是统计数，跟着实时链路走（Q111）。
	let feed = $state<LiveFeed | null>(null);
	onMount(() => {
		const live = new LiveFeed();
		feed = live;
		return live.close;
	});

	onMount(() => {
		void load();
		let timer: ReturnType<typeof setInterval> | undefined;

		function stop() {
			if (timer !== undefined) clearInterval(timer);
			timer = undefined;
		}

		function start() {
			stop();
			timer = setInterval(() => void load(), refreshMs);
		}

		function onVisibility() {
			if (document.visibilityState === 'visible') {
				void load();
				start();
			} else {
				stop();
			}
		}

		if (document.visibilityState === 'visible') start();
		document.addEventListener('visibilitychange', onVisibility);
		return () => {
			stop();
			document.removeEventListener('visibilitychange', onVisibility);
		};
	});

	/** 成功率 = 成功 /（成功 + 失败），取消的不算进分母（Q55）。 */
	function successRate(t: StatsTotalsView): string {
		const decided = t.successCount + t.failureCount;
		return decided === 0 ? '—' : percent(t.successCount / decided);
	}

	/** 尝试失败率：因上游原因失败的 Attempt 占全部 Attempt（Q57）。 */
	function attemptFailureRate(t: StatsTotalsView): string {
		return t.attemptCount === 0 ? '—' : percent(t.failedAttemptCount / t.attemptCount);
	}

	function percent(ratio: number): string {
		return (ratio * 100).toFixed(ratio === 1 || ratio === 0 ? 0 : 1) + '%';
	}

	function tokens(t: StatsTotalsView): number {
		return t.inputTokens + t.cacheReadTokens + t.cacheWrite5mTokens + t.cacheWrite1hTokens + t.outputTokens;
	}

	const rows = $derived.by((): StatsGroupView[] => {
		const groups = stats ? [...stats.groups[tab]] : [];
		if (sort === 'requests') {
			groups.sort((a, b) => b.totals.requestCount - a.totals.requestCount || a.name.localeCompare(b.name));
		} else if (sort === 'name') {
			groups.sort((a, b) => a.name.localeCompare(b.name));
		}
		return groups; // 服务端已按费用降序
	});

	const labels = $derived(stats?.points.map((p) => p.label) ?? []);
	const pending = $derived(stats?.points.map((p) => p.inProgress) ?? []);
	const requestSeries = $derived(
		stats
			? [
					{ label: '成功', color: 'var(--ok)', values: stats.points.map((p) => p.successCount) },
					{ label: '失败', color: 'var(--danger)', values: stats.points.map((p) => p.failureCount) },
					{ label: '取消', color: 'var(--muted)', values: stats.points.map((p) => p.canceledCount) }
				]
			: []
	);
	const costSeries = $derived(
		stats ? [{ label: '费用', color: 'var(--accent)', values: stats.points.map((p) => p.costNanoUsd) }] : []
	);
	const formatRequests = (value: number) => formatCount(value);
	const formatChartCost = (value: number) => formatCost(value);
	const columns = $derived(tab === 'channels' ? 9 : 8);
</script>

<svelte:head>
	<title>总览 · 南天门</title>
</svelte:head>

<h1>总览</h1>

<div class="toolbar">
	<div role="group" aria-label="时间范围" class="segmented">
		{#each ranges as r (r.value)}
			<button type="button" aria-pressed={range === r.value} onclick={() => choose(r.value)}>{r.label}</button>
		{/each}
	</div>
	<button type="button" onclick={() => void load()} disabled={loading}>{loading ? '刷新中…' : '刷新'}</button>
	<span class="muted">
		{#if stats}
			按 {stats.timeZone} 时间，{stats.granularity === 'hour' ? '每小时一个点' : '每天一个点'}
		{/if}
		{#if loadedAt}
			· 更新于 {formatTime(loadedAt.toISOString())}
		{/if}
	</span>
</div>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

{#if syncEvents && syncEvents.unacknowledgedEntered > 0}
	<p class="notice" role="status">
		{formatCount(syncEvents.unacknowledgedEntered)} 个新上游模型进入了选路。
		<a href={resolve('/model-sync-events')}>看看是哪些</a>
		<button type="button" onclick={acknowledgeSync} disabled={acknowledging}>知道了</button>
	</p>
{/if}

{#if stats}
	{@const t = stats.totals}
	<section aria-label="合计" class="cards">
		<a class="card" href={resolve('/requests')}>
			<span class="label">进行中</span>
			<strong>{feed ? formatCount(feed.requests.size) : '—'}</strong>
			<span class="muted">{feed && !feed.connected ? '实时连接已断开' : '现在，不随时间范围变'}</span>
		</a>
		<div class="card">
			<span class="label">请求</span>
			<strong>{formatCount(t.requestCount)}</strong>
			<span class="muted">取消 {formatCount(t.canceledCount)}</span>
		</div>
		<div class="card">
			<span class="label">成功率</span>
			<strong>{successRate(t)}</strong>
			<span class="muted">失败 {formatCount(t.failureCount)}</span>
		</div>
		<div class="card">
			<span class="label">费用</span>
			<strong>{formatCost(t.costNanoUsd)}</strong>
			<span class="muted">
				{#if t.unpricedCount > 0}另有 {formatCount(t.unpricedCount)} 次无单价{/if}
				{#if t.unpricedCount > 0 && t.estimatedCount > 0}·{/if}
				{#if t.estimatedCount > 0}其中 {formatCount(t.estimatedCount)} 次按估算用量{/if}
			</span>
		</div>
		<div class="card">
			<span class="label">首字时间 P50</span>
			<strong>{formatMs(t.timeToFirstContentP50Ms)}</strong>
			<span class="muted">P95 {formatMs(t.timeToFirstContentP95Ms)} · 流式 {formatCount(t.timeToFirstContentSampleCount)} 次</span>
		</div>
		<div class="card wide">
			<span class="label">Token</span>
			<strong>{formatCount(tokens(t))}</strong>
			<span class="muted">
				输入 {formatCount(t.inputTokens)} · 缓存读 {formatCount(t.cacheReadTokens)} · 缓存写 5 分钟
				{formatCount(t.cacheWrite5mTokens)} · 缓存写 1 小时 {formatCount(t.cacheWrite1hTokens)} · 输出
				{formatCount(t.outputTokens)}
			</span>
		</div>
	</section>

	<section class="panel">
		<BarChart title="请求数" {labels} inProgress={pending} series={requestSeries} format={formatRequests} />
	</section>
	<section class="panel">
		<BarChart title="费用（美元）" {labels} inProgress={pending} series={costSeries} format={formatChartCost} />
	</section>

	<section class="panel">
		<div class="toolbar">
			<div role="tablist" aria-label="分组方式" class="segmented">
				{#each tabs as item (item.value)}
					<button
						type="button"
						role="tab"
						aria-selected={tab === item.value}
						aria-controls="stats-groups"
						onclick={() => (tab = item.value)}
					>
						{item.label}
					</button>
				{/each}
			</div>
			<label>
				排序
				<select bind:value={sort}>
					<option value="cost">费用</option>
					<option value="requests">请求数</option>
					<option value="name">名字</option>
				</select>
			</label>
		</div>

		<div id="stats-groups" role="tabpanel">
		<table>
			<thead>
				<tr>
					<th scope="col">{tabs.find((x) => x.value === tab)?.label}</th>
					<th scope="col" class="num">请求</th>
					<th scope="col" class="num">成功率</th>
					{#if tab === 'channels'}
						<th scope="col" class="num">尝试失败率</th>
					{/if}
					<th scope="col" class="num">Token</th>
					<th scope="col" class="num">费用</th>
					<th scope="col" class="num">无单价</th>
					<th scope="col" class="num">首字 P50</th>
					<th scope="col" class="num">首字 P95</th>
				</tr>
			</thead>
			<tbody>
				{#each rows as row (row.key)}
					<tr>
						<th scope="row">
							{row.name}
							{#if row.deleted}<span class="badge">已删除</span>{/if}
						</th>
						<td class="num">{formatCount(row.totals.requestCount)}</td>
						<td class="num">{successRate(row.totals)}</td>
						{#if tab === 'channels'}
							<td class="num">{attemptFailureRate(row.totals)}</td>
						{/if}
						<td class="num">{formatCount(tokens(row.totals))}</td>
						<td class="num">{formatCost(row.totals.costNanoUsd)}</td>
						<td class="num">{row.totals.unpricedCount > 0 ? formatCount(row.totals.unpricedCount) : '—'}</td>
						<td class="num">{formatMs(row.totals.timeToFirstContentP50Ms)}</td>
						<td class="num">{formatMs(row.totals.timeToFirstContentP95Ms)}</td>
					</tr>
				{:else}
					<tr>
						<td colspan={columns} class="muted">这段时间没有请求。</td>
					</tr>
				{/each}
			</tbody>
		</table>
		</div>
		{#if tab === 'channels'}
			<p class="muted">
				成功率按请求的最终结果算；尝试失败率按每次上游调用算。一个渠道总被换掉时，尝试失败率会先升高。
			</p>
		{/if}
	</section>
{:else if loading}
	<p class="muted">正在加载…</p>
{/if}

<style>
	.segmented {
		display: inline-flex;
	}

	.segmented button {
		border-radius: 0;
	}

	.segmented button:first-child {
		border-radius: 6px 0 0 6px;
	}

	.segmented button:last-child {
		border-radius: 0 6px 6px 0;
	}

	.segmented button[aria-pressed='true'],
	.segmented button[aria-selected='true'] {
		background: var(--accent);
		color: var(--accent-text);
		border-color: var(--accent);
	}

	.cards {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr));
		gap: 0.75rem;
		margin: 1rem 0;
	}

	.card {
		display: flex;
		flex-direction: column;
		gap: 0.25rem;
		padding: 0.75rem 1rem;
		border: 1px solid var(--border);
		border-radius: 8px;
		background: var(--panel);
	}

	.card.wide {
		grid-column: 1 / -1;
	}

	a.card {
		color: inherit;
		text-decoration: none;
	}

	a.card:hover {
		border-color: var(--accent, currentColor);
	}

	.card strong {
		font-size: 1.5rem;
		font-variant-numeric: tabular-nums;
	}

	.card .label {
		color: var(--muted);
		font-size: 0.875rem;
	}

	.muted {
		color: var(--muted);
		font-size: 0.875rem;
	}
</style>
