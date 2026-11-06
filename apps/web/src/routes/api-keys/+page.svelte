<script lang="ts">
	import { resolve } from '$app/paths';
	import { onMount } from 'svelte';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import { formatTime } from '#lib/format.ts';
	import type { CreatedApiKeyView, ApiKeyView } from '#lib/types.ts';

	let apiKeys = $state<ApiKeyView[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);

	let name = $state('');
	let nameError = $state<string | null>(null);
	let creating = $state(false);
	let created = $state<CreatedApiKeyView | null>(null);
	let copied = $state(false);

	let busyId = $state<number | null>(null);

	async function load() {
		loading = true;
		error = null;
		try {
			apiKeys = await api.get<ApiKeyView[]>('/api/api-keys');
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	onMount(load);

	async function create(event: SubmitEvent) {
		event.preventDefault();
		creating = true;
		nameError = null;
		created = null;
		copied = false;
		try {
			created = await api.post<CreatedApiKeyView>('/api/api-keys', { name: name.trim() });
			name = '';
			await load();
		} catch (e) {
			if (e instanceof ApiError && e.field('name')) {
				nameError = e.field('name') ?? null;
			} else {
				nameError = messageOf(e);
			}
		} finally {
			creating = false;
		}
	}

	async function copy() {
		if (!created) return;
		try {
			await navigator.clipboard.writeText(created.secret);
			copied = true;
		} catch {
			copied = false;
		}
	}

	async function toggle(apiKey: ApiKeyView) {
		busyId = apiKey.id;
		error = null;
		try {
			await api.patch('/api/api-keys/' + apiKey.id, { enabled: !apiKey.enabled });
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}

	async function remove(apiKey: ApiKeyView) {
		if (!confirm(`删除 API 密钥「${apiKey.name}」？用它的客户端会立刻被拒绝，这一步撤销不了。`)) return;
		busyId = apiKey.id;
		error = null;
		try {
			await api.delete('/api/api-keys/' + apiKey.id);
			if (created?.view.id === apiKey.id) created = null;
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}
</script>

<svelte:head>
	<title>API 密钥 · 南天门</title>
</svelte:head>

<h1>API 密钥</h1>
<p class="muted">客户端用 API 密钥调用 <code>/v1</code>，放在 <code>Authorization: Bearer</code> 或 <code>x-api-key</code> 头里。点名字设置费用上限、模型和渠道限制。</p>

<form class="toolbar" onsubmit={create}>
	<label class="field">
		<span>新 API 密钥的名字</span>
		<input
			bind:value={name}
			maxlength="200"
			required
			aria-invalid={nameError ? 'true' : undefined}
			aria-describedby={nameError ? 'apiKey-name-error' : undefined}
		/>
	</label>
	<button class="primary" type="submit" disabled={creating || name.trim().length === 0}>
		{creating ? '创建中…' : '创建 API 密钥'}
	</button>
</form>
{#if nameError}
	<p id="apiKey-name-error" class="error" role="alert">{nameError}</p>
{/if}

{#if created}
	<div class="notice" role="status">
		<p>
			API 密钥「{created.view.name}」已创建。完整的 API 密钥只显示这一次，关掉这条提示后就再也看不到了：
		</p>
		<p><code class="secret">{created.secret}</code></p>
		<div class="actions">
			<button type="button" onclick={copy}>{copied ? '已复制' : '复制'}</button>
			<button type="button" onclick={() => (created = null)}>我已保存，关闭</button>
		</div>
	</div>
{/if}

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

<table>
	<thead>
		<tr>
			<th scope="col">名字</th>
			<th scope="col">前缀</th>
			<th scope="col">状态</th>
			<th scope="col">创建时间</th>
			<th scope="col">最近使用</th>
			<th scope="col"><span class="visually-hidden">操作</span></th>
		</tr>
	</thead>
	<tbody>
		{#each apiKeys as apiKey (apiKey.id)}
			<tr>
				<td><a href={resolve('/api-keys/[id]', { id: String(apiKey.id) })}>{apiKey.name}</a></td>
				<td class="mono">{apiKey.prefix}…</td>
				<td>
					<span class={apiKey.enabled ? 'ok' : 'muted'}>{apiKey.enabled ? '启用' : '已停用'}</span>
				</td>
				<td>{formatTime(apiKey.createdAt)}</td>
				<td>{formatTime(apiKey.lastUsedAt)}</td>
				<td class="row-actions">
					<button type="button" onclick={() => toggle(apiKey)} disabled={busyId === apiKey.id}>
						{apiKey.enabled ? '停用' : '启用'}
					</button>
					<button
						type="button"
						class="danger"
						onclick={() => remove(apiKey)}
						disabled={busyId === apiKey.id}
						aria-label={`删除 API 密钥 ${apiKey.name}`}
					>
						删除
					</button>
				</td>
			</tr>
		{:else}
			<tr>
				<td colspan="6" class="muted">{loading ? '加载中…' : '还没有 API 密钥。'}</td>
			</tr>
		{/each}
	</tbody>
</table>

<style>
	.secret {
		overflow-wrap: anywhere;
		user-select: all;
	}

	.row-actions {
		display: flex;
		gap: 0.4rem;
		justify-content: flex-end;
	}
</style>
