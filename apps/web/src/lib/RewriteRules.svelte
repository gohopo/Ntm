<script lang="ts">
	import { api, ApiError } from '#lib/api.ts';
	import type { RewriteOperation, RewriteRuleView, RewriteTarget } from '#lib/types.ts';

	let {
		channelId,
		rules: saved,
		onSaved
	}: { channelId: number; rules: RewriteRuleView[]; onSaved?: (rules: RewriteRuleView[]) => void } = $props();

	const maxRules = 20;

	const operationLabels: Record<RewriteOperation, string> = {
		set: '设置',
		remove: '删除',
		default: '补充'
	};

	const targetLabels: Record<RewriteTarget, string> = {
		body: '请求体',
		header: '请求头'
	};

	let rows = $state<RewriteRuleView[]>([]);
	let busy = $state(false);
	let error = $state<ApiError | null>(null);
	let notice = $state<string | null>(null);

	// 父页面重新加载详情时，用服务器上的规则重置编辑中的表格。
	$effect(() => {
		rows = saved.map((rule) => ({ ...rule }));
		error = null;
	});

	function add() {
		rows = [...rows, { target: 'body', operation: 'set', path: '', value: '' }];
		notice = null;
	}

	function removeAt(index: number) {
		rows = rows.filter((_, i) => i !== index);
		notice = null;
	}

	function move(index: number, delta: number) {
		const to = index + delta;
		if (to < 0 || to >= rows.length) return;
		const next = [...rows];
		[next[index], next[to]] = [next[to], next[index]];
		rows = next;
		notice = null;
	}

	function fieldError(index: number, field: string): string | undefined {
		return error?.field(`rules[${index}].${field}`);
	}

	async function save(event: SubmitEvent) {
		event.preventDefault();
		busy = true;
		error = null;
		notice = null;
		try {
			const body = rows.map((rule) => ({
				target: rule.target,
				operation: rule.operation,
				path: rule.path.trim(),
				value: rule.operation === 'remove' ? null : rule.value
			}));
			const result = await api.put<RewriteRuleView[]>(`/api/channels/${channelId}/rewrite-rules`, { rules: body });
			notice = result.length === 0 ? '已清空，不再改写。' : `已保存 ${result.length} 条规则。`;
			onSaved?.(result);
		} catch (e) {
			error = e instanceof ApiError ? e : new ApiError(0, { title: String(e) });
		} finally {
			busy = false;
		}
	}

	const generalError = $derived(
		error && Object.keys(error.fields).every((key) => !/^rules\[\d+\]\./.test(key)) ? error.summary() : null
	);
</script>

<form class="panel" onsubmit={save} novalidate>
	<!-- 中文换行会被折成空格，所以整段写在一行里。 -->
	<p class="muted intro">规则按顺序改写发往这个渠道的上游请求，作用在网关最终发出的请求上，所以字段名要按上游格式写。「补充」只在请求里没有这一项时才写入。认证头、Content-Type 以及请求体顶层的 <code>model</code>、<code>stream</code> 不能改写。</p>

	{#if rows.length > 0}
		<table class="rules">
			<thead>
				<tr>
					<th>操作</th>
					<th>位置</th>
					<th>路径或头名</th>
					<th>值</th>
					<th></th>
				</tr>
			</thead>
			<tbody>
				{#each rows as rule, index (index)}
					<tr>
						<td>
							<select bind:value={rule.operation} aria-label="操作">
								{#each Object.entries(operationLabels) as [value, label] (value)}
									<option {value}>{label}</option>
								{/each}
							</select>
						</td>
						<td>
							<select bind:value={rule.target} aria-label="位置">
								{#each Object.entries(targetLabels) as [value, label] (value)}
									<option {value}>{label}</option>
								{/each}
							</select>
						</td>
						<td>
							<input
								type="text"
								class="mono"
								bind:value={rule.path}
								aria-label="路径或头名"
								placeholder={rule.target === 'body' ? '例如 thinking.type' : '例如 anthropic-beta'}
							/>
							{#if fieldError(index, 'path')}<span class="error">{fieldError(index, 'path')}</span>{/if}
						</td>
						<td>
							{#if rule.operation === 'remove'}
								<span class="muted">—</span>
							{:else}
								<input
									type="text"
									class="mono"
									bind:value={rule.value}
									aria-label="值"
									placeholder={rule.target === 'body' ? '一段 JSON，例如 "disabled"、0.2、{"a":1}' : '纯文本'}
								/>
								{#if fieldError(index, 'value')}<span class="error">{fieldError(index, 'value')}</span>{/if}
							{/if}
						</td>
						<td class="row-actions">
							<button type="button" onclick={() => move(index, -1)} disabled={index === 0} aria-label="上移">↑</button>
							<button type="button" onclick={() => move(index, 1)} disabled={index === rows.length - 1} aria-label="下移">↓</button>
							<button type="button" class="danger" onclick={() => removeAt(index)}>删除</button>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{:else}
		<p class="muted">还没有规则，请求原样转给上游。</p>
	{/if}

	<div class="actions">
		<button type="button" onclick={add} disabled={rows.length >= maxRules}>添加规则</button>
		<button class="primary" type="submit" disabled={busy}>{busy ? '保存中…' : '保存改写规则'}</button>
		{#if notice}<span class="ok" role="status">{notice}</span>{/if}
		{#if rows.length >= maxRules}<span class="muted">每个渠道最多 {maxRules} 条。</span>{/if}
	</div>
	{#if generalError}
		<p class="alert" role="alert">{generalError}</p>
	{/if}
</form>

<style>
	.intro {
		margin-top: 0;
	}

	.rules {
		margin-bottom: 0.75rem;
	}

	.rules td {
		vertical-align: top;
	}

	.rules input {
		width: 100%;
		min-width: 12rem;
	}

	.mono {
		font-family: var(--mono, ui-monospace, monospace);
	}

	.row-actions {
		white-space: nowrap;
	}

	.row-actions button {
		margin-left: 0.25rem;
	}

	.error {
		display: block;
		color: var(--danger, #b42318);
		font-size: 0.85rem;
		margin-top: 0.25rem;
	}
</style>
