<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import { formatCost, formatCount, formatTime } from '#lib/format.ts';
	import { poll } from '#lib/poll.ts';
	import DebugSwitch from '#lib/DebugSwitch.svelte';
	import type {
		ApiKeyDetailView,
		ApiKeyRuntimeView,
		ChannelView,
		ModelView
	} from '#lib/types.ts';

	type ChannelMode = 'none' | 'allow' | 'block';

	const shownFields = [
		'costLimitUsd',
		'maxConcurrency',
		'allowedModels',
		'allowedChannelIds',
		'blockedChannelIds'
	];

	const id = $derived(page.params.id ?? '');

	let detail = $state<ApiKeyDetailView | null>(null);
	let loadError = $state<string | null>(null);
	let runtime = $state<ApiKeyRuntimeView | null>(null);

	let models = $state<ModelView[]>([]);
	let channels = $state<ChannelView[]>([]);

	let costLimitUsd = $state<number | null>(null);
	let maxConcurrency = $state<number | null>(null);
	let allowedModels = $state<string[]>([]);
	let channelModes = $state<Record<number, ChannelMode>>({});

	let saving = $state(false);
	let saveError = $state<ApiError | null>(null);
	let saveNotice = $state<string | null>(null);

	function fill(value: ApiKeyDetailView) {
		detail = value;
		const r = value.restrictions;
		costLimitUsd = r.costLimitNanoUsd === null ? null : r.costLimitNanoUsd / 1_000_000_000;
		maxConcurrency = r.maxConcurrency;
		allowedModels = [...r.allowedModels];
		const modes: Record<number, ChannelMode> = {};
		for (const channelId of r.allowedChannelIds) modes[channelId] = 'allow';
		for (const channelId of r.blockedChannelIds) modes[channelId] = 'block';
		channelModes = modes;
	}

	async function loadRuntime() {
		try {
			runtime = await api.get<ApiKeyRuntimeView>('/api/api-keys/' + id + '/runtime');
		} catch {
			runtime = null;
		}
	}

	$effect(() => {
		const current = id;
		detail = null;
		loadError = null;
		saveError = null;
		saveNotice = null;
		api.get<ApiKeyDetailView>('/api/api-keys/' + encodeURIComponent(current)).then(
			(value) => fill(value),
			(e: unknown) => (loadError = messageOf(e))
		);
		api.get<ModelView[]>('/api/models').then(
			(value) => (models = value),
			() => (models = [])
		);
		api.get<ChannelView[]>('/api/channels').then(
			(value) => (channels = value),
			() => (channels = [])
		);
	});

	$effect(() => {
		void id;
		void loadRuntime();
		return poll(loadRuntime, 10_000);
	});

	// 名单里可能有已经删掉的模型和渠道。照样列出来，让人能看到并去掉（删渠道不清名单，见 Q106 的讨论）。
	const modelNames = $derived([
		...models.map((m) => m.name),
		...allowedModels.filter((name) => !models.some((m) => m.name === name))
	]);

	const channelRows = $derived([
		...channels.map((c) => ({ id: c.id, name: c.name, gone: false })),
		...Object.keys(channelModes)
			.map(Number)
			.filter((channelId) => channelModes[channelId] !== 'none' && !channels.some((c) => c.id === channelId))
			.map((channelId) => ({ id: channelId, name: `#${channelId}`, gone: true }))
	]);

	const hasAllowList = $derived(Object.values(channelModes).includes('allow'));

	function toggleModel(name: string, on: boolean) {
		allowedModels = on ? [...allowedModels, name] : allowedModels.filter((m) => m !== name);
	}

	function idsWith(mode: ChannelMode): number[] {
		return Object.entries(channelModes)
			.filter(([, value]) => value === mode)
			.map(([key]) => Number(key));
	}

	async function save(event: SubmitEvent) {
		event.preventDefault();
		saving = true;
		saveError = null;
		saveNotice = null;
		try {
			await api.put('/api/api-keys/' + id + '/restrictions', {
				costLimitUsd,
				maxConcurrency,
				allowedModels,
				allowedChannelIds: idsWith('allow'),
				blockedChannelIds: idsWith('block')
			});
			fill(await api.get<ApiKeyDetailView>('/api/api-keys/' + id));
			await loadRuntime();
			saveNotice = '限制已保存。';
		} catch (e) {
			saveError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			saving = false;
		}
	}
</script>

<svelte:head>
	<title>{detail?.apiKey.name ?? 'API 密钥'} · 南天门</title>
</svelte:head>

<p><a href={resolve('/api-keys')}>← 返回 API 密钥列表</a></p>
<h1>{detail?.apiKey.name ?? 'API 密钥'}</h1>

{#if loadError}
	<p class="alert" role="alert">{loadError}</p>
{:else if !detail}
	<p class="muted">加载中…</p>
{:else}
	<dl class="facts">
		<dt>前缀</dt>
		<dd class="mono">{detail.apiKey.prefix}…</dd>
		<dt>状态</dt>
		<dd><span class={detail.apiKey.enabled ? 'ok' : 'muted'}>{detail.apiKey.enabled ? '启用' : '已停用'}</span></dd>
		<dt>创建时间</dt>
		<dd>{formatTime(detail.apiKey.createdAt)}</dd>
		<dt>最近使用</dt>
		<dd>{formatTime(detail.apiKey.lastUsedAt)}</dd>
	</dl>

	{#if runtime}
		<div class="runtime">
			<span>
				累计已花 <strong>{formatCost(runtime.spentNanoUsd)}</strong>
				{#if runtime.costLimitNanoUsd !== null}
					/ {formatCost(runtime.costLimitNanoUsd)}
					{#if runtime.spentNanoUsd >= runtime.costLimitNanoUsd}
						<span class="badge error">已用完</span>
					{/if}
				{:else}
					<span class="muted">（不限）</span>
				{/if}
			</span>
			<span>
				进行中 <strong>{formatCount(runtime.inFlight)}</strong>{detail.restrictions.maxConcurrency !== null
					? ` / ${detail.restrictions.maxConcurrency}`
					: ''}
			</span>
			{#if runtime.unpricedCount > 0}
				<span class="badge warn" title="没有单价的请求按 0 计入上限">
					{formatCount(runtime.unpricedCount)} 个请求无单价，没算进上限
				</span>
			{/if}
		</div>
	{/if}

	{#if saveNotice}
		<p class="notice" role="status">{saveNotice}</p>
	{/if}
	{#if saveError}
		<p class="alert" role="alert">{saveError.summary(shownFields)}</p>
	{/if}

	<form class="panel" onsubmit={save} novalidate>
		<div class="form-grid">
			<label class="field">
				<span>费用上限（美元，可选）</span>
				<input type="number" bind:value={costLimitUsd} min="0.000001" step="0.000001" placeholder="不限" />
				<span class="hint">
					终身累计，按倍率后的实际费用算，不按天重置。用完后客户端收到 429，调高上限即可恢复。
					只在请求开始前检查，实际花费可能略超。
				</span>
				{#if saveError?.field('costLimitUsd')}<span class="error">{saveError.field('costLimitUsd')}</span>{/if}
			</label>
			<label class="field">
				<span>并发上限（可选）</span>
				<input type="number" bind:value={maxConcurrency} min="1" step="1" placeholder="不限" />
				<span class="hint">同时进行的请求数，满了直接回 429。</span>
				{#if saveError?.field('maxConcurrency')}
					<span class="error">{saveError.field('maxConcurrency')}</span>
				{/if}
			</label>
		</div>

		<h2>模型</h2>
		<p class="muted">
			一个都不勾表示所有下发模型都能用。勾了之后只能用勾选的，其他模型回 403，<code>/v1/models</code> 里也看不到。
		</p>
		<div class="choices">
			{#each modelNames as name (name)}
				<label class="check">
					<input
						type="checkbox"
						checked={allowedModels.includes(name)}
						onchange={(event) => toggleModel(name, event.currentTarget.checked)}
					/>
					<span>{name}</span>
					{#if !models.some((m) => m.name === name)}<span class="muted">（已不存在）</span>{/if}
				</label>
			{:else}
				<p class="muted">还没有下发模型。</p>
			{/each}
		</div>
		{#if saveError?.field('allowedModels')}<p class="error">{saveError.field('allowedModels')}</p>{/if}

		<h2>渠道</h2>
		<p class="muted">
			有「只用」的渠道时，只在这些渠道里选；「不用」的渠道总是排除。都不选表示所有渠道都能用。
			{#if hasAllowList}<strong>现在只会用标了「只用」的渠道。</strong>{/if}
		</p>
		<table>
			<thead>
				<tr>
					<th scope="col">渠道</th>
					<th scope="col">这个 API 密钥</th>
				</tr>
			</thead>
			<tbody>
				{#each channelRows as row (row.id)}
					<tr>
						<td>
							{#if row.gone}
								<span class="muted">已删除的渠道 {row.name}</span>
							{:else}
								<a href={resolve('/channels/[id]', { id: String(row.id) })}>{row.name}</a>
							{/if}
						</td>
						<td>
							<select
								value={channelModes[row.id] ?? 'none'}
								onchange={(event) =>
									(channelModes = {
										...channelModes,
										[row.id]: event.currentTarget.value as ChannelMode
									})}
								aria-label={`渠道 ${row.name} 对这个 API 密钥`}
							>
								<option value="none">—</option>
								<option value="allow">只用</option>
								<option value="block">不用</option>
							</select>
						</td>
					</tr>
				{:else}
					<tr><td colspan="2" class="muted">还没有渠道。</td></tr>
				{/each}
			</tbody>
		</table>
		{#if saveError?.field('allowedChannelIds')}<p class="error">{saveError.field('allowedChannelIds')}</p>{/if}
		{#if saveError?.field('blockedChannelIds')}<p class="error">{saveError.field('blockedChannelIds')}</p>{/if}

		<div class="actions">
			<button class="primary" type="submit" disabled={saving}>{saving ? '保存中…' : '保存限制'}</button>
		</div>
	</form>

	<h2>调试原文</h2>
	<div class="panel">
		<DebugSwitch url={`/api/api-keys/${detail.apiKey.id}/debug-capture`} label="这个 API 密钥" />
	</div>
{/if}

<style>
	.facts {
		display: grid;
		grid-template-columns: max-content 1fr;
		gap: 0.3rem 1rem;
		margin: 0 0 1rem;
	}

	.facts dt {
		color: var(--muted);
	}

	.facts dd {
		margin: 0;
	}

	.runtime {
		display: flex;
		flex-wrap: wrap;
		gap: 1rem;
		align-items: center;
		margin-bottom: 1rem;
	}

	.choices {
		display: flex;
		flex-wrap: wrap;
		gap: 0.4rem 1.2rem;
	}
</style>
