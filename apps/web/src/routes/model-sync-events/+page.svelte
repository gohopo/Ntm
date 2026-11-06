<script lang="ts">
	import { resolve } from '$app/paths';
	import { api, messageOf } from '#lib/api.ts';
	import { formatTime } from '#lib/format.ts';
	import { onMount } from 'svelte';
	import type { ModelSyncEventListView } from '#lib/types.ts';

	const kindLabels = {
		enteredRouting: '进入选路',
		leftRouting: '离开选路'
	};

	let list = $state<ModelSyncEventListView | null>(null);
	let error = $state<string | null>(null);
	let busy = $state(false);

	async function load() {
		try {
			list = await api.get<ModelSyncEventListView>('/api/model-sync-events');
			error = null;
		} catch (e) {
			error = messageOf(e);
		}
	}

	async function acknowledge() {
		busy = true;
		try {
			await api.post('/api/model-sync-events/acknowledge', {});
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busy = false;
		}
	}

	onMount(load);
</script>

<svelte:head><title>模型同步事件 · 南天门</title></svelte:head>

<h1>模型同步事件</h1>
<p class="muted">
	模型同步改动了选路时记一条：新发现的上游模型正好被已有的匹配规则选中，或者消失的上游模型本来在选路里。只留 30 天。
</p>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

{#if list}
	{#if list.unacknowledgedEntered > 0}
		<p class="notice" role="status">
			有 {list.unacknowledgedEntered} 个新上游模型进入了选路。
			<button type="button" onclick={acknowledge} disabled={busy}>知道了</button>
		</p>
	{/if}

	{#if list.events.length === 0}
		<p class="muted">还没有事件。</p>
	{:else}
		<table>
			<thead>
				<tr>
					<th scope="col">时间</th>
					<th scope="col">变化</th>
					<th scope="col">渠道</th>
					<th scope="col">上游模型</th>
					<th scope="col">下发模型</th>
				</tr>
			</thead>
			<tbody>
				{#each list.events as event (event.id)}
					<tr class={event.acknowledged ? 'seen' : ''}>
						<td>{formatTime(event.occurredAt)}</td>
						<td>
							{kindLabels[event.kind]}
							{#if !event.acknowledged && event.kind === 'enteredRouting'}
								<span class="badge">新</span>
							{/if}
						</td>
						<td>
							<a href={resolve('/channels/[id]', { id: String(event.channelId) })}>
								{event.channelName}
							</a>
						</td>
						<td><code>{event.upstreamModel}</code></td>
						<td>{event.publishedModels}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
{:else if !error}
	<p class="muted">加载中…</p>
{/if}

<style>
	.notice button {
		margin-left: 0.5rem;
	}

	.seen {
		color: var(--muted);
	}
</style>
