<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api, messageOf } from '#lib/api.ts';
	import { formatTime, requestKindLabels } from '#lib/format.ts';
	import type { ModelView } from '#lib/types.ts';

	let models = $state<ModelView[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let busyId = $state<number | null>(null);

	async function load() {
		loading = true;
		error = null;
		try {
			models = await api.get<ModelView[]>('/api/models');
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	onMount(load);

	async function toggle(model: ModelView) {
		busyId = model.id;
		error = null;
		try {
			await api.patch('/api/models/' + model.id, { enabled: !model.enabled });
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}
</script>

<svelte:head>
	<title>下发模型 · 南天门</title>
</svelte:head>

<div class="heading">
	<h1>下发模型</h1>
	<a class="button primary" href={resolve('/models/[id]', { id: 'new' })}>新建下发模型</a>
</div>
<p class="muted">客户端看到和请求的模型名。每个下发模型用匹配规则从渠道的上游模型里挑出候选。</p>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

<table>
	<thead>
		<tr>
			<th scope="col">模型名</th>
			<th scope="col">显示名</th>
			<th scope="col">类型</th>
			<th scope="col" class="num">规则</th>
			<th scope="col" class="num">候选</th>
			<th scope="col">状态</th>
			<th scope="col">修改时间</th>
			<th scope="col"><span class="visually-hidden">操作</span></th>
		</tr>
	</thead>
	<tbody>
		{#each models as model (model.id)}
			<tr>
				<td class="mono"><a href={resolve('/models/[id]', { id: String(model.id) })}>{model.name}</a></td>
				<td>{model.displayName ?? '—'}</td>
				<td>
					{#if model.requestKind === 'chat'}{requestKindLabels.chat}{:else}<span class="badge"
							>{requestKindLabels[model.requestKind]}</span
						>{/if}
				</td>
				<td class="num">
					{model.ruleCount}
					{#if model.problemCount > 0}
						<span class="badge error" title="有规则写错了">{model.problemCount} 条有问题</span>
					{/if}
				</td>
				<td class="num">
					<span class={model.candidateCount === 0 ? 'error' : ''}>{model.candidateCount}</span>
				</td>
				<td>
					<span class={model.enabled ? 'ok' : 'muted'}>{model.enabled ? '已下发' : '未下发'}</span>
				</td>
				<td>{formatTime(model.updatedAt)}</td>
				<td>
					<button type="button" onclick={() => toggle(model)} disabled={busyId === model.id}>
						{model.enabled ? '停止下发' : '下发'}
					</button>
				</td>
			</tr>
		{:else}
			<tr>
				<td colspan="8" class="muted">{loading ? '加载中…' : '还没有下发模型，客户端现在什么模型都用不了。'}</td>
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
