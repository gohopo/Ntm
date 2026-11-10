<script lang="ts">
	import { onMount } from 'svelte';
	import { api, messageOf } from '#lib/api.ts';
	import { formatTime } from '#lib/format.ts';

	type Change = { entity: string; entityId: string | null; name: string | null; state: string; fields: string[] };
	type Entry = {
		id: number; occurredAt: string; remoteAddress: string | null; action: string; succeeded: boolean;
		targetType: string | null; targetId: string | null; targetName: string | null; changes: Change[];
	};
	type AuditPage = { items: Entry[]; nextCursor: string | null };

	const actions: Record<string, string> = {
		'auth.login': '登录', 'auth.logout': '退出登录', 'auth.logout-all': '退出所有设备',
		'auth.password': '修改密码', 'cli.reset-admin-password': '命令行重置密码'
	};
	const entities: Record<string, string> = {
		Channel: '渠道', ChannelKey: '渠道密钥', ApiKey: 'API 密钥', PublishedModel: '下发模型',
		MatchRule: '匹配规则', UpstreamModel: '上游模型', GatewaySettings: '全局设置', PriceRow: '单价',
		AdminAccount: '管理员', ModelSyncEvent: '模型同步事件'
	};
	const states: Record<string, string> = { added: '新增', modified: '修改', deleted: '删除' };
	const sensitive: Record<string, string> = { Secret: '密钥已更换', Proxy: '代理地址已更换', Password: '密码已更换' };

	let items = $state<Entry[]>([]);
	let options = $state<string[]>([]);
	let action = $state('');
	let nextCursor = $state<string | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);
	let optionsError = $state<string | null>(null);

	async function load(more = false) {
		if (loading) return;
		loading = true;
		error = null;
		try {
			const query = new URLSearchParams({ limit: '50' });
			if (action) query.set('action', action);
			if (more && nextCursor) query.set('cursor', nextCursor);
			const result = await api.get<AuditPage>('/api/audit?' + query);
			items = more ? [...items, ...result.items] : result.items;
			nextCursor = result.nextCursor;
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	async function loadActions() {
		try {
			options = await api.get<string[]>('/api/audit/actions');
			optionsError = null;
		} catch (e) {
			optionsError = messageOf(e);
		}
	}

	function refresh() {
		void load();
		void loadActions();
	}

	onMount(refresh);
</script>

<svelte:head><title>操作记录 · 南天门</title></svelte:head>

<h1>操作记录</h1>
<p class="muted">记录管理员的登录和配置改动，与客户端的请求记录分开。只记改了哪些字段，不记字段值；默认保留 180 天，可由服务器配置调整。</p>
<div class="toolbar">
	<label class="field">
		<span>动作</span>
		<select bind:value={action} disabled={loading} onchange={() => { items = []; nextCursor = null; void load(); }}>
			<option value="">全部动作</option>
			{#each options as value (value)}<option value={value}>{actions[value] ?? value}</option>{/each}
		</select>
	</label>
	<button type="button" onclick={refresh} disabled={loading}>刷新</button>
</div>
{#if optionsError}<p class="alert" role="alert">读取动作列表失败：{optionsError}</p>{/if}
{#if error}<p class="alert" role="alert">{error}</p>{/if}
<table>
	<thead><tr>
		<th scope="col">时间</th><th scope="col">来源 IP</th><th scope="col">动作</th>
		<th scope="col">对象</th><th scope="col">改动</th>
	</tr></thead>
	<tbody>
		{#each items as entry (entry.id)}
			<tr>
				<td>{formatTime(entry.occurredAt)}</td>
				<td class="mono">{entry.remoteAddress ?? (entry.action.startsWith('cli.') ? '命令行' : '—')}</td>
				<td>{actions[entry.action] ?? entry.action}
					{#if !entry.succeeded}<span class="badge error">失败</span>{/if}
				</td>
				<td>{entry.targetName ?? entry.targetType ?? '—'}{#if entry.targetId} <span class="muted">#{entry.targetId}</span>{/if}</td>
				<td>
					{#each entry.changes as change}
						<div class="change">
							{states[change.state] ?? change.state} {entities[change.entity] ?? change.entity}
							{change.name ?? ''}{#if change.entityId} <span class="muted">#{change.entityId}</span>{/if}
							{#if change.fields.length > 0}<div class="muted">{change.fields.map((field) => sensitive[field] ?? field).join('、')}</div>{/if}
						</div>
					{:else}<span class="muted">—</span>{/each}
				</td>
			</tr>
		{:else}<tr><td colspan="5" class="muted">{loading ? '加载中…' : '没有符合条件的操作记录。'}</td></tr>{/each}
	</tbody>
</table>
{#if nextCursor}<div class="actions"><button type="button" disabled={loading} onclick={() => load(true)}>{loading ? '加载中…' : '加载更多'}</button></div>{/if}

<style>
	td { overflow-wrap: anywhere; }
	.change + .change { margin-top: 0.5rem; }
</style>
