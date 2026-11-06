<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import {
		blockReasonLabels,
		formatTime,
		publishableKinds,
		requestKindEndpoints,
		requestKindLabels
	} from '#lib/format.ts';
	import type {
		ChannelView,
		MatchKind,
		ModelDetailView,
		ModelInput,
		RequestKind,
		RoutePreviewView,
		RoutingStateView,
		RuleInput,
		RuleView
	} from '#lib/types.ts';

	/** 编辑中的一条规则。排除模式在界面上是多行文本，提交时拆成数组。 */
	interface RuleDraft {
		key: number;
		kind: MatchKind;
		pattern: string;
		channelId: number | null;
		excludedChannelIds: number[];
		excludedPatternsText: string;
		priority: number | null;
		weight: number | null;
		enabled: boolean;
	}

	const id = $derived(page.params.id ?? 'new');
	const isNew = $derived(id === 'new');

	let nextKey = 1;
	let loaded = $state(false);
	let loadError = $state<string | null>(null);
	let channels = $state<ChannelView[]>([]);

	let name = $state('');
	let displayName = $state('');
	let enabled = $state(false);
	let requestKind = $state<RequestKind>('chat');
	let rules = $state<RuleDraft[]>([]);

	let saving = $state(false);
	let saveError = $state<ApiError | null>(null);
	let saveNotice = $state<string | null>(null);
	let pendingNotice: string | null = null;

	let preview = $state<RoutePreviewView | null>(null);
	let previewError = $state<string | null>(null);
	let previewing = $state(false);
	let previewSeq = 0;

	// 「现在的选路」（Q259）：进入页面时读一次，另有刷新按钮，不自动轮询。
	let routing = $state<RoutingStateView | null>(null);
	let routingError = $state<string | null>(null);
	let routingLoading = $state(false);

	async function loadRouting() {
		if (isNew) return;
		routingLoading = true;
		try {
			routing = await api.get<RoutingStateView>('/api/models/' + encodeURIComponent(id) + '/routing');
			routingError = null;
		} catch (e) {
			routingError = messageOf(e);
		} finally {
			routingLoading = false;
		}
	}

	function percent(share: number): string {
		return (share * 100).toFixed(share >= 0.1 || share === 0 ? 0 : 1) + '%';
	}

	const channelNames = $derived(new Map(channels.map((c) => [c.id, c.name])));

	function draftOf(rule?: RuleView): RuleDraft {
		return {
			key: nextKey++,
			kind: rule?.kind ?? 'exact',
			pattern: rule?.pattern ?? '',
			channelId: rule?.channelId ?? null,
			excludedChannelIds: rule ? [...rule.excludedChannelIds] : [],
			excludedPatternsText: rule ? rule.excludedPatterns.join('\n') : '',
			priority: rule?.priority ?? null,
			weight: rule?.weight ?? null,
			enabled: rule?.enabled ?? true
		};
	}

	function fill(value: ModelDetailView) {
		name = value.model.name;
		displayName = value.model.displayName ?? '';
		enabled = value.model.enabled;
		requestKind = value.model.requestKind;
		rules = value.rules.map((r) => draftOf(r));
		preview = value.route;
	}

	function toInput(): ModelInput {
		return {
			name: name.trim(),
			displayName: displayName.trim() || null,
			requestKind,
			enabled,
			rules: toRules()
		};
	}

	function toRules(): RuleInput[] {
		return rules.map(
				(r): RuleInput => ({
					kind: r.kind,
					pattern: r.pattern,
					channelId: r.channelId,
					excludedChannelIds: r.excludedChannelIds,
					excludedPatterns: r.excludedPatternsText
						.split(/\r?\n/)
						.map((p) => p.trim())
						.filter((p) => p.length > 0),
					priority: numberOrNull(r.priority),
					weight: numberOrNull(r.weight),
					enabled: r.enabled
				})
		);
	}

	/** 清空的数字框绑定出来可能是 null、undefined 或 NaN，一律当「沿用渠道的」。 */
	function numberOrNull(value: number | null | undefined): number | null {
		return typeof value === 'number' && Number.isFinite(value) ? value : null;
	}

	onMount(() => {
		api.get<ChannelView[]>('/api/channels').then(
			(list) => (channels = list),
			() => (channels = [])
		);
	});

	$effect(() => {
		const current = id;
		loaded = false;
		loadError = null;
		saveError = null;
		saveNotice = pendingNotice;
		pendingNotice = null;
		if (current === 'new') {
			name = '';
			displayName = '';
			enabled = false;
			requestKind = 'chat';
			rules = [draftOf()];
			preview = null;
			loaded = true;
			return;
		}
		routing = null;
		routingError = null;
		api.get<ModelDetailView>('/api/models/' + encodeURIComponent(current)).then(
			(value) => {
				fill(value);
				loaded = true;
				void loadRouting();
			},
			(e: unknown) => (loadError = messageOf(e))
		);
	});

	// 规则或类型一变就（防抖后）用真实匹配器跑一遍预览。名字和开关不影响候选。
	$effect(() => {
		if (!loaded) return;
		const body = JSON.stringify({ requestKind, rules: toRules() });
		const seq = ++previewSeq;
		const timer = setTimeout(async () => {
			previewing = true;
			try {
				const result = await api.post<RoutePreviewView>('/api/models/preview', JSON.parse(body));
				if (seq === previewSeq) {
					preview = result;
					previewError = null;
				}
			} catch (e) {
				if (seq === previewSeq) previewError = messageOf(e);
			} finally {
				if (seq === previewSeq) previewing = false;
			}
		}, 300);
		return () => clearTimeout(timer);
	});

	function addRule() {
		rules.push(draftOf());
	}

	function removeRule(index: number) {
		rules.splice(index, 1);
	}

	function moveRule(index: number, delta: number) {
		const target = index + delta;
		if (target < 0 || target >= rules.length) return;
		const [rule] = rules.splice(index, 1);
		rules.splice(target, 0, rule);
	}

	function ruleError(index: number, field: string): string | undefined {
		return saveError?.field(`rules[${index}].${field}`);
	}

	const shownFields = $derived([
		'name',
		'displayName',
		'requestKind',
		...rules.flatMap((_, i) =>
			['kind', 'pattern', 'channelId', 'excludedChannelIds', 'excludedPatterns', 'priority', 'weight'].map(
				(f) => `rules[${i}].${f}`
			)
		)
	]);

	async function save(event: SubmitEvent) {
		event.preventDefault();
		saving = true;
		saveError = null;
		saveNotice = null;
		try {
			if (isNew) {
				const created = await api.post<ModelDetailView>('/api/models', toInput());
				pendingNotice = created.model.enabled
					? '已创建并下发。'
					: '已创建。确认下面的候选没问题后，勾上「下发」再保存，客户端才看得到它。';
				await goto(resolve('/models/[id]', { id: String(created.model.id) }), { replaceState: true });
			} else {
				fill(await api.put<ModelDetailView>('/api/models/' + id, toInput()));
				saveNotice = '已保存。';
				void loadRouting();
			}
		} catch (e) {
			saveError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			saving = false;
		}
	}

	async function remove() {
		if (!confirm(`删除下发模型「${name}」和它的全部规则？客户端会立刻用不了这个模型。`)) return;
		try {
			await api.delete('/api/models/' + id);
			await goto(resolve('/models'));
		} catch (e) {
			saveError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		}
	}
</script>

<svelte:head>
	<title>{isNew ? '新建下发模型' : name || '下发模型'} · 南天门</title>
</svelte:head>

<p><a href={resolve('/models')}>← 返回下发模型列表</a></p>
<h1>{isNew ? '新建下发模型' : name || '下发模型'}</h1>

{#if loadError}
	<p class="alert" role="alert">{loadError}</p>
{:else if !loaded}
	<p class="muted">加载中…</p>
{:else}
	{#if saveNotice}
		<p class="notice" role="status">{saveNotice}</p>
	{/if}
	{#if saveError}
		<p class="alert" role="alert">{saveError.summary(shownFields)}</p>
	{/if}

	<div class="layout">
		<form class="panel editor" onsubmit={save} novalidate>
			<div class="form-grid">
				<label class="field">
					<span>模型名</span>
					<input class="mono" bind:value={name} maxlength="300" required spellcheck="false" />
					<span class="hint">客户端请求里写的名字，不能有空格。</span>
					{#if saveError?.field('name')}<span class="error">{saveError.field('name')}</span>{/if}
				</label>
				<label class="field">
					<span>显示名（可选）</span>
					<input bind:value={displayName} maxlength="300" />
					{#if saveError?.field('displayName')}
						<span class="error">{saveError.field('displayName')}</span>
					{/if}
				</label>
				<label class="field">
					<span>类型</span>
					<select bind:value={requestKind}>
						{#each publishableKinds as kind (kind)}
							<option value={kind}>{requestKindLabels[kind]}</option>
						{/each}
					</select>
					<span class="hint">
						客户端用 {requestKindEndpoints[requestKind]} 请求它。{#if requestKind !== 'chat'}嵌入和图片只转给 OpenAI Chat
							格式的渠道，原样透传。{/if}
					</span>
					{#if saveError?.field('requestKind')}
						<span class="error">{saveError.field('requestKind')}</span>
					{/if}
				</label>
				<label class="check">
					<input type="checkbox" bind:checked={enabled} />
					<span>下发（客户端能看到并使用）</span>
				</label>
			</div>

			<h2>匹配规则</h2>
			<p class="muted hint">
				按顺序先到先得：同一个渠道的同一个上游模型只归第一条匹配到它的规则。排除模式按这条规则自己的匹配方式（精确或正则）判断。
			</p>

			<ol class="rules">
				{#each rules as rule, i (rule.key)}
					{@const stats = preview?.rules.find((r) => r.ordinal === i + 1)}
					<li class="rule" aria-label={`规则 ${i + 1}`}>
						<div class="rule-head">
							<strong>规则 {i + 1}</strong>
							{#if stats}
								{#if stats.problem}
									<span class="badge error">{stats.problem}</span>
								{:else}
									<span class="badge {stats.matchCount > 0 ? 'ok' : 'muted'}">匹配 {stats.matchCount}</span>
								{/if}
							{/if}
							<span class="spacer"></span>
							<button type="button" onclick={() => moveRule(i, -1)} disabled={i === 0} aria-label={`规则 ${i + 1} 上移`}>↑</button>
							<button
								type="button"
								onclick={() => moveRule(i, 1)}
								disabled={i === rules.length - 1}
								aria-label={`规则 ${i + 1} 下移`}>↓</button
							>
							<button type="button" class="danger" onclick={() => removeRule(i)} aria-label={`删除规则 ${i + 1}`}>删除</button>
						</div>

						<div class="form-grid">
							<label class="field">
								<span>匹配方式</span>
								<select bind:value={rule.kind}>
									<option value="exact">精确</option>
									<option value="regex">正则</option>
								</select>
							</label>
							<label class="field wide">
								<span>模式</span>
								<input class="mono" bind:value={rule.pattern} maxlength="1000" spellcheck="false" />
								{#if stats?.effectivePattern && rule.kind === 'regex'}
									<span class="hint">实际按 <code>{stats.effectivePattern}</code> 整体匹配。</span>
								{/if}
								{#if ruleError(i, 'pattern')}<span class="error">{ruleError(i, 'pattern')}</span>{/if}
							</label>
							<label class="field">
								<span>只用这个渠道</span>
								<select bind:value={rule.channelId}>
									<option value={null}>所有渠道</option>
									{#each channels as channel (channel.id)}
										<option value={channel.id}>{channel.name}</option>
									{/each}
								</select>
								{#if ruleError(i, 'channelId')}<span class="error">{ruleError(i, 'channelId')}</span>{/if}
							</label>
							<label class="field">
								<span>优先级（可选）</span>
								<input type="number" bind:value={rule.priority} min="-1000" max="1000" step="1" placeholder="沿用渠道的" />
								{#if ruleError(i, 'priority')}<span class="error">{ruleError(i, 'priority')}</span>{/if}
							</label>
							<label class="field">
								<span>权重（可选）</span>
								<input type="number" bind:value={rule.weight} min="1" max="1000" step="1" placeholder="沿用渠道的" />
								{#if ruleError(i, 'weight')}<span class="error">{ruleError(i, 'weight')}</span>{/if}
							</label>
							<label class="field">
								<span>排除的渠道</span>
								<select multiple bind:value={rule.excludedChannelIds} size={Math.min(4, Math.max(2, channels.length))}>
									{#each channels as channel (channel.id)}
										<option value={channel.id}>{channel.name}</option>
									{/each}
								</select>
								{#if ruleError(i, 'excludedChannelIds')}
									<span class="error">{ruleError(i, 'excludedChannelIds')}</span>
								{/if}
							</label>
							<label class="field wide">
								<span>排除模式（每行一个）</span>
								<textarea bind:value={rule.excludedPatternsText} rows="2" spellcheck="false"></textarea>
								{#if ruleError(i, 'excludedPatterns')}
									<span class="error">{ruleError(i, 'excludedPatterns')}</span>
								{/if}
							</label>
							<label class="check">
								<input type="checkbox" bind:checked={rule.enabled} />
								<span>启用这条规则</span>
							</label>
						</div>
					</li>
				{:else}
					<li class="muted">没有规则，这个模型不会有候选。</li>
				{/each}
			</ol>

			<div class="actions">
				<button type="button" onclick={addRule}>添加规则</button>
				<span class="spacer"></span>
				<button class="primary" type="submit" disabled={saving}>
					{saving ? '保存中…' : isNew ? '创建' : '保存'}
				</button>
				{#if !isNew}
					<button type="button" class="danger" onclick={remove}>删除下发模型</button>
				{/if}
			</div>
		</form>

		<section class="panel preview" aria-labelledby="preview-heading" aria-busy={previewing}>
			<h2 id="preview-heading">候选预览</h2>
			<p class="muted hint">用和真实请求同一套匹配器，按当前生效的渠道配置算出来；保存前就能看。</p>
			{#if previewError}
				<p class="alert" role="alert">{previewError}</p>
			{/if}
			{#if preview}
				<p aria-live="polite">
					共 <strong>{preview.candidateCount}</strong> 个候选{previewing ? '（更新中…）' : ''}
				</p>
				{#each preview.priorityGroups as group (group.priority)}
					<h3>优先级 {group.priority}</h3>
					<table>
						<thead>
							<tr>
								<th scope="col">渠道</th>
								<th scope="col">上游模型</th>
								<th scope="col" class="num">权重</th>
								<th scope="col" class="num">规则</th>
							</tr>
						</thead>
						<tbody>
							{#each group.candidates as candidate (candidate.channelId + ':' + candidate.upstreamModel)}
								<tr>
									<td>
										{channelNames.get(candidate.channelId) ?? candidate.channelName}
										{#if candidate.channelPriority !== group.priority}
											<span class="muted small">规则改了优先级（渠道本身是 {candidate.channelPriority}）</span>
										{/if}
									</td>
									<td class="mono">{candidate.upstreamModel}</td>
									<td class="num">{candidate.weight}</td>
									<td class="num">{candidate.ruleOrdinal}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{/each}
				{#if preview.excluded.length > 0}
					<h3>匹配到但用不了</h3>
					<table>
						<thead>
							<tr>
								<th scope="col">渠道</th>
								<th scope="col">上游模型</th>
								<th scope="col">原因</th>
							</tr>
						</thead>
						<tbody>
							{#each preview.excluded as item (item.channelId + ':' + item.upstreamModel)}
								<tr>
									<td>{item.channelName}</td>
									<td class="mono">{item.upstreamModel}</td>
									<td>{item.reason}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{/if}
			{:else}
				<p class="muted">{previewing ? '计算中…' : '填好规则后这里会显示候选。'}</p>
			{/if}
		</section>
	</div>

	{#if !isNew}
		<section class="panel routing" aria-labelledby="routing-heading" aria-busy={routingLoading}>
			<div class="routing-head">
				<h2 id="routing-heading">现在的选路</h2>
				<a href={resolve('/requests') + '?model=' + encodeURIComponent(name)}>看它的请求</a>
				<span class="spacer"></span>
				<button type="button" onclick={loadRouting} disabled={routingLoading}>
					{routingLoading ? '读取中…' : '刷新'}
				</button>
			</div>
			<p class="muted hint">
				按保存的配置和此刻的冷却、限制、健康度算出来，只读，不影响轮询。请求落在第一个有可用候选的档里，
				档内按有效权重（权重 × 健康系数）分；其余档是备用。同一个对话会尽量落回上次的候选（亲和），这里不模拟。
				健康度只算近 30 分钟、只在内存里，网关重启后从零算。
			</p>
			{#if routingError}
				<p class="alert" role="alert">{routingError}</p>
			{/if}
			{#if routing}
				{#if !routing.modelEnabled}
					<p class="notice">这个模型还没下发，客户端现在请求不到它；下面是下发后的样子。</p>
				{/if}
				<p class="muted small">读取于 {formatTime(routing.at)}</p>
				{#each routing.tiers as tier (tier.priority)}
					<h3>
						优先级 {tier.priority}
						{#if tier.active}<span class="badge ok">当前</span>{:else}<span class="badge">备用</span>{/if}
					</h3>
					<table>
						<thead>
							<tr>
								<th scope="col">渠道</th>
								<th scope="col">上游模型</th>
								<th scope="col" class="num">权重</th>
								<th scope="col" class="num">近 30 分钟</th>
								<th scope="col" class="num">健康系数</th>
								<th scope="col" class="num">有效权重</th>
								<th scope="col" class="num">预计份额</th>
								<th scope="col">状态</th>
							</tr>
						</thead>
						<tbody>
							{#each tier.candidates as candidate (candidate.channelId + ':' + candidate.upstreamModel)}
								<tr>
									<td>
										<a href={resolve('/channels/[id]', { id: String(candidate.channelId) })}>{candidate.channelName}</a>
										{#if candidate.channelPriority !== tier.priority}
											<span class="muted small">规则改了优先级（渠道本身是 {candidate.channelPriority}）</span>
										{/if}
									</td>
									<td class="mono">{candidate.upstreamModel}</td>
									<td class="num">{candidate.weight}</td>
									<td class="num">
										{#if candidate.successes + candidate.failures === 0}
											<span class="muted">没有样本</span>
										{:else}
											成功 {candidate.successes} / 失败 {candidate.failures}
										{/if}
									</td>
									<td class="num" class:warn={candidate.healthFactor < 0.8}>{candidate.healthFactor.toFixed(2)}</td>
									<td class="num">{candidate.effectiveWeight}</td>
									<td class="num">{candidate.share === null ? '—' : percent(candidate.share)}</td>
									<td>
										{#if candidate.block === null}
											<span class="ok">可用</span>
										{:else}
											<span class="warn">{blockReasonLabels[candidate.block]}</span>
											{#if candidate.recoversAt}
												<span class="muted small">到 {formatTime(candidate.recoversAt)}</span>
											{/if}
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{:else}
					<p class="muted">没有候选。看上面「候选预览」里匹配到但用不了的原因。</p>
				{/each}
				{#if routing.tiers.length > 0 && !routing.tiers.some((t) => t.active)}
					<p class="alert">所有候选现在都用不了，请求会直接返回 503。</p>
				{/if}
			{:else if routingLoading}
				<p class="muted">读取中…</p>
			{/if}
		</section>
	{/if}
{/if}

<style>
	.layout {
		display: grid;
		grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
		gap: 1rem;
		align-items: start;
	}

	@media (max-width: 60rem) {
		.layout {
			grid-template-columns: 1fr;
		}
	}

	.preview {
		position: sticky;
		top: 1rem;
	}

	.preview h2 {
		margin-top: 0;
	}

	h3 {
		font-size: 1rem;
		margin: 1rem 0 0.4rem;
	}

	.hint {
		font-size: 0.85rem;
	}

	.small {
		font-size: 0.8rem;
	}

	.routing {
		margin-top: 1rem;
	}

	.routing-head {
		display: flex;
		align-items: baseline;
		gap: 0.75rem;
	}

	.routing-head h2 {
		margin: 0;
	}

	.rules {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 0.75rem;
	}

	.rule {
		border: 1px solid var(--border);
		border-radius: 6px;
		padding: 0.75rem;
	}

	.rule-head {
		display: flex;
		align-items: center;
		gap: 0.5rem;
		margin-bottom: 0.5rem;
	}

	.spacer {
		flex: 1;
	}

	.wide {
		grid-column: span 2;
	}

	@media (max-width: 40rem) {
		.wide {
			grid-column: auto;
		}
	}
</style>
