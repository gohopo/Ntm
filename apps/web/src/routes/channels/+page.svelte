<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api, messageOf } from '#lib/api.ts';
	import { apiFormatLabels, lowHealthThreshold } from '#lib/format.ts';
	import type { ChannelView } from '#lib/types.ts';

	let channels = $state<ChannelView[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let busyId = $state<number | null>(null);

	async function load() {
		loading = true;
		error = null;
		try {
			channels = await api.get<ChannelView[]>('/api/channels');
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	onMount(load);

	async function toggle(channel: ChannelView) {
		busyId = channel.id;
		error = null;
		try {
			await api.patch('/api/channels/' + channel.id, { enabled: !channel.enabled });
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}
</script>

<svelte:head>
	<title>渠道 · 南天门</title>
</svelte:head>

<div class="heading">
	<h1>渠道</h1>
	<a class="button primary" href={resolve('/channels/[id]', { id: 'new' })}>新建渠道</a>
</div>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

<table>
	<thead>
		<tr>
			<th scope="col">名字</th>
			<th scope="col">地址</th>
			<th scope="col">格式</th>
			<th scope="col" class="num">优先级</th>
			<th scope="col" class="num">权重</th>
			<th scope="col" class="num">倍率</th>
			<th scope="col" class="num">渠道密钥（启用 / 全部）</th>
			<th scope="col" class="num">上游模型</th>
			<th scope="col">状态</th>
			<th scope="col"><span class="visually-hidden">操作</span></th>
		</tr>
	</thead>
	<tbody>
		{#each channels as channel (channel.id)}
			<tr>
				<td><a href={resolve('/channels/[id]', { id: String(channel.id) })}>{channel.name}</a></td>
				<td class="mono url">{channel.requestUrl ?? channel.baseUrl}</td>
				<td>{apiFormatLabels[channel.apiFormat]}</td>
				<td class="num">{channel.priority}</td>
				<td class="num">{channel.weight}</td>
				<td class="num">×{channel.multiplier}</td>
				<td class="num">
					<span class={channel.enabledKeyCount === 0 ? 'error' : ''}>{channel.enabledKeyCount}</span>
					/ {channel.keyCount}
				</td>
				<td class="num">{channel.upstreamModelCount}</td>
				<td>
					<span class={channel.enabled ? 'ok' : 'muted'}>{channel.enabled ? '启用' : '已停用'}</span>
					{#if channel.coolingCount > 0}
						<span class="badge warn">冷却 {channel.coolingCount}</span>
					{/if}
					{#if channel.lowestHealth != null && channel.lowestHealth < lowHealthThreshold}
						<span class="badge warn" title="近 30 分钟最差的那个上游模型的健康系数">健康 {channel.lowestHealth.toFixed(2)}</span>
					{/if}
				</td>
				<td>
					<button type="button" onclick={() => toggle(channel)} disabled={busyId === channel.id}>
						{channel.enabled ? '停用' : '启用'}
					</button>
				</td>
			</tr>
		{:else}
			<tr>
				<td colspan="10" class="muted">{loading ? '加载中…' : '还没有渠道。'}</td>
			</tr>
		{/each}
	</tbody>
</table>

<style>
	.heading {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
	}

	.url {
		overflow-wrap: anywhere;
		max-width: 22rem;
	}

	.button {
		display: inline-block;
		padding: 0.35rem 0.8rem;
		border-radius: 4px;
		text-decoration: none;
		border: 1px solid var(--accent);
	}

	.button.primary {
		background: var(--accent);
		color: var(--accent-text);
	}
</style>
