<script lang="ts">
	import { onMount } from 'svelte';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import { formatCount, formatTime } from '#lib/format.ts';
	import type {
		CatalogStatusView,
		CatalogView,
		PricePageView,
		PriceRowView,
		PriceSetView
	} from '#lib/types.ts';

	type Filter = 'all' | 'unpriced' | 'manual' | 'unused';
	type PriceKey = keyof PriceSetView;

	const priceParts: { key: PriceKey; label: string }[] = [
		{ key: 'inputPrice', label: '输入' },
		{ key: 'cacheReadPrice', label: '缓存读' },
		{ key: 'cacheWrite5mPrice', label: '缓存写 5 分钟' },
		{ key: 'cacheWrite1hPrice', label: '缓存写 1 小时' },
		{ key: 'outputPrice', label: '输出' },
		{ key: 'thinkingOutputPrice', label: '思考输出' }
	];

	const filters: { value: Filter; label: string }[] = [
		{ value: 'all', label: '全部' },
		{ value: 'unpriced', label: '无单价' },
		{ value: 'manual', label: '人工' },
		{ value: 'unused', label: '没被使用' }
	];

	let status = $state<CatalogStatusView | null>(null);
	let refreshing = $state(false);
	let refreshError = $state<string | null>(null);

	let data = $state<PricePageView | null>(null);
	let loading = $state(true);
	let error = $state<string | null>(null);

	let filter = $state<Filter>('all');
	let search = $state('');
	let page = $state(1);

	// 编辑中的行。单价用字符串存，空串就是「不知道」（Q65）。
	interface GroupDraft {
		inputTokensOver: string;
		prices: Record<PriceKey, string>;
	}
	let editingId = $state<number | null>(null);
	let draft = $state<Record<PriceKey, string>>(emptyDraft());
	let groups = $state<GroupDraft[]>([]);
	// 每张图片的单价（Q230）：不属于 Token 单价那一组，长上下文也不分档。
	let imageDraft = $state('');
	let fieldErrors = $state<Record<string, string>>({});
	let saveError = $state<string | null>(null);
	let saving = $state(false);
	let busyId = $state<number | null>(null);

	const pageCount = $derived(data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1);

	function emptyDraft(): Record<PriceKey, string> {
		return { inputPrice: '', cacheReadPrice: '', cacheWrite5mPrice: '', cacheWrite1hPrice: '',
			outputPrice: '',
			thinkingOutputPrice: ''
		};
	}

	function toDraft(prices: PriceSetView): Record<PriceKey, string> {
		const result = emptyDraft();
		for (const { key } of priceParts) {
			result[key] = prices[key] === null ? '' : String(prices[key]);
		}
		return result;
	}

	async function loadStatus() {
		try {
			status = (await api.get<CatalogView>('/api/price-catalog')).status;
		} catch (e) {
			refreshError = messageOf(e);
		}
	}

	async function load() {
		loading = true;
		error = null;
		const query = new URLSearchParams({ filter, page: String(page) });
		if (search.trim()) query.set('search', search.trim());
		try {
			data = await api.get<PricePageView>('/api/prices?' + query.toString());
		} catch (e) {
			error = messageOf(e);
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		void loadStatus();
		void load();
	});

	function applyFilters(event: SubmitEvent) {
		event.preventDefault();
		page = 1;
		void load();
	}

	function go(target: number) {
		page = target;
		void load();
	}

	async function refresh() {
		refreshing = true;
		refreshError = null;
		try {
			status = await api.post<CatalogStatusView>('/api/price-catalog/refresh');
			if (status.lastError) refreshError = status.lastError;
			await load();
		} catch (e) {
			refreshError = messageOf(e);
		} finally {
			refreshing = false;
		}
	}

	function startEdit(row: PriceRowView) {
		editingId = row.id;
		draft = toDraft(row.prices);
		groups = row.longContext.map((g) => ({
			inputTokensOver: String(g.inputTokensOver),
			prices: toDraft(g.prices)
		}));
		imageDraft = row.imagePrice === null ? '' : String(row.imagePrice);
		fieldErrors = {};
		saveError = null;
	}

	function cancelEdit() {
		editingId = null;
		fieldErrors = {};
		saveError = null;
	}

	/** 把输入框的字符串变成数字；空串是 null。返回 undefined 表示不是数字。 */
	function parse(value: string): number | null | undefined {
		const text = value.trim();
		if (text === '') return null;
		const number = Number(text);
		return Number.isFinite(number) ? number : undefined;
	}

	function parseSet(
		source: Record<PriceKey, string>,
		prefix: string,
		errors: Record<string, string>
	): PriceSetView {
		const result: PriceSetView = {
			inputPrice: null,
			cacheReadPrice: null,
			cacheWrite5mPrice: null,
			cacheWrite1hPrice: null,
			outputPrice: null,
			thinkingOutputPrice: null
		};
		for (const { key } of priceParts) {
			const parsed = parse(source[key]);
			if (parsed === undefined) {
				errors[prefix + key] =
					key === 'thinkingOutputPrice'
						? '请输入数字，或者留空表示和输出相同。'
						: '请输入数字，或者留空表示不知道。';
			} else {
				result[key] = parsed;
			}
		}
		return result;
	}

	async function save(row: PriceRowView, event: SubmitEvent) {
		event.preventDefault();
		const errors: Record<string, string> = {};
		const prices = parseSet(draft, '', errors);
		const imagePrice = parse(imageDraft);
		if (imagePrice === undefined) errors['imagePrice'] = '请输入数字，或者留空表示不按张计费。';
		const longContext = groups.map((group, i) => {
			const over = parse(group.inputTokensOver);
			if (over === undefined || over === null || !Number.isInteger(over)) {
				errors[`longContext[${i}].inputTokensOver`] = '门槛要填整数个 Token。';
			}
			return {
				inputTokensOver: typeof over === 'number' ? over : 0,
				prices: parseSet(group.prices, `longContext[${i}].prices.`, errors)
			};
		});
		fieldErrors = errors;
		saveError = null;
		if (Object.keys(errors).length > 0) return;

		saving = true;
		try {
			await api.put<PriceRowView>('/api/prices/' + row.id, {
				...prices,
				longContext,
				imagePrice: imagePrice ?? null
			});
			editingId = null;
			await load();
		} catch (e) {
			if (e instanceof ApiError && Object.keys(e.fields).length > 0) {
				fieldErrors = Object.fromEntries(Object.entries(e.fields).map(([k, v]) => [k, v[0] ?? '']));
				saveError = e.summary(Object.keys(e.fields));
				if (saveError === e.message) saveError = null;
			} else {
				saveError = messageOf(e);
			}
		} finally {
			saving = false;
		}
	}

	async function restoreAuto(row: PriceRowView) {
		if (!confirm(`把「${row.upstreamModel}」恢复成自动？人工填的单价会被目录里的覆盖，目录里没有就清空。`)) return;
		busyId = row.id;
		error = null;
		try {
			await api.post('/api/prices/' + row.id + '/auto');
			if (editingId === row.id) editingId = null;
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}

	async function remove(row: PriceRowView) {
		if (!confirm(`删除「${row.provider} / ${row.upstreamModel}」这一行单价？`)) return;
		busyId = row.id;
		error = null;
		try {
			await api.delete('/api/prices/' + row.id);
			await load();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busyId = null;
		}
	}

	function sourceLabel(row: PriceRowView): string {
		if (row.source === 'manual') return '人工';
		if (row.candidates.length > 0) return '有歧义';
		if (row.unpriced) return '无单价';
		return '自动';
	}

	function show(value: number | null, key?: PriceKey): string {
		if (value === null) return key === 'thinkingOutputPrice' ? '同输出' : '—';
		return '$' + value;
	}
</script>

<svelte:head>
	<title>单价 · 南天门</title>
</svelte:head>

<h1>单价</h1>
<p class="muted">
	每个渠道的 (供应商, 上游模型) 一行，单位是美元 / 每百万 Token（「每张图片」是美元 / 张，只能人工填），是原厂官方价；渠道的倍率另算。
	留空是「不知道」，这时候用到这一项的请求算不出费用；0 是免费。
	「思考输出」例外：模型输出了思考过程时整个输出都按它算，留空就是和输出相同。
</p>

<section class="panel" aria-labelledby="catalog-heading">
	<h2 id="catalog-heading">价目目录</h2>
	<p>
		自动单价来自 <a href="https://models.dev" rel="noreferrer" target="_blank">models.dev</a>（MIT 许可），
		只取渠道用到的供应商。改过的行不会被刷新覆盖。
	</p>
	{#if status}
		<dl class="facts">
			<dt>最近取到</dt>
			<dd>{status.fetchedAt ? formatTime(status.fetchedAt) : '还没取到'}</dd>
			<dt>最近一次尝试</dt>
			<dd>{formatTime(status.lastAttemptAt)}</dd>
			<dt>自动刷新</dt>
			<dd>{status.autoRefresh ? '开' : '关（只能手动刷新）'}</dd>
			<dt>供应商数</dt>
			<dd>{status.providerCount}</dd>
		</dl>
		{#if status.lastError}
			<p class="alert" role="alert">上次刷新失败：{status.lastError}</p>
		{/if}
	{/if}
	{#if refreshError && refreshError !== status?.lastError}
		<p class="alert" role="alert">{refreshError}</p>
	{/if}
	<div class="actions">
		<button type="button" onclick={refresh} disabled={refreshing || status?.refreshing}>
			{refreshing || status?.refreshing ? '刷新中…' : '立即刷新'}
		</button>
	</div>
</section>

<form class="toolbar" onsubmit={applyFilters}>
	<label class="field">
		<span>筛选</span>
		<select
			value={filter}
			onchange={(event) => {
				filter = event.currentTarget.value as Filter;
				go(1);
			}}
		>
			{#each filters as option (option.value)}
				<option value={option.value}>{option.label}</option>
			{/each}
		</select>
	</label>
	<label class="field">
		<span>搜索供应商或模型</span>
		<input type="search" bind:value={search} maxlength="300" />
	</label>
	<button type="submit">搜索</button>
</form>

{#if error}
	<p class="alert" role="alert">{error}</p>
{/if}

<table>
	<caption class="visually-hidden">单价，美元 / 每百万 Token；每张图片那一列是美元 / 张</caption>
	<thead>
		<tr>
			<th scope="col">供应商</th>
			<th scope="col">上游模型</th>
			<th scope="col">来源</th>
			{#each priceParts as part (part.key)}
				<th scope="col" class="num">{part.label}</th>
			{/each}
			<th scope="col" class="num">每张图片</th>
			<th scope="col">长上下文</th>
			<th scope="col">渠道</th>
			<th scope="col" class="num">近 7 天无单价</th>
			<th scope="col"><span class="visually-hidden">操作</span></th>
		</tr>
	</thead>
	<tbody>
		{#each data?.items ?? [] as row (row.id)}
			<tr class:unused={row.channels.length === 0}>
				<td>{row.provider}</td>
				<td class="mono">{row.upstreamModel}</td>
				<td>
					<span class="badge" class:warn={row.unpriced}>{sourceLabel(row)}</span>
					{#if row.matchedModelId && row.matchedModelId !== row.upstreamModel}
						<div class="muted small">按 {row.matchedModelId}</div>
					{/if}
					{#if row.candidates.length > 0}
						<div class="muted small">候选：{row.candidates.join('、')}</div>
					{/if}
				</td>
				{#each priceParts as part (part.key)}
					<td class="num mono">{show(row.prices[part.key], part.key)}</td>
				{/each}
				<td class="num mono">{show(row.imagePrice)}</td>
				<td>
					{#if row.longContext.length === 0}
						—
					{:else}
						{row.longContext.length} 组（超过 {row.longContext
							.map((g) => formatCount(g.inputTokensOver))
							.join(' / ')}）
					{/if}
				</td>
				<td>{row.channels.length === 0 ? '没被使用' : row.channels.join('、')}</td>
				<td class="num">{row.unpricedRequests === 0 ? '—' : formatCount(row.unpricedRequests)}</td>
				<td class="row-actions">
					<button
						type="button"
						onclick={() => (editingId === row.id ? cancelEdit() : startEdit(row))}
						aria-expanded={editingId === row.id}
						aria-label={`编辑 ${row.upstreamModel} 的单价`}
					>
						{editingId === row.id ? '收起' : '编辑'}
					</button>
					{#if row.source === 'manual'}
						<button type="button" onclick={() => restoreAuto(row)} disabled={busyId === row.id}>
							恢复自动
						</button>
					{/if}
					{#if row.channels.length === 0}
						<button
							type="button"
							class="danger"
							onclick={() => remove(row)}
							disabled={busyId === row.id}
							aria-label={`删除 ${row.upstreamModel} 的单价`}
						>
							删除
						</button>
					{/if}
				</td>
			</tr>
			{#if editingId === row.id}
				<tr class="editor-row">
					<td colspan="15">
						<form class="editor" onsubmit={(event) => save(row, event)}>
							<p class="muted">保存后这一行变成人工，之后刷新目录不会再改它。</p>
							<div class="form-grid">
								{#each priceParts as part (part.key)}
									<label class="field">
										<span>{part.label}</span>
										<input
											inputmode="decimal"
											bind:value={draft[part.key]}
											aria-invalid={fieldErrors[part.key] ? 'true' : undefined}
										/>
										{#if fieldErrors[part.key]}
											<small class="error">{fieldErrors[part.key]}</small>
										{/if}
									</label>
								{/each}
							</div>

							<h3>每张图片</h3>
							<p class="muted">
								只对图片生成有用。填了就按返回的图片张数 × 这个价 × 渠道倍率计费，不再看 Token；留空就按上面的 Token
								单价算，上游没给用量就是无单价。尺寸和质量不分档。
							</p>
							<div class="form-grid">
								<label class="field">
									<span>每张图片（美元）</span>
									<input
										inputmode="decimal"
										bind:value={imageDraft}
										aria-invalid={fieldErrors['imagePrice'] ? 'true' : undefined}
									/>
									{#if fieldErrors['imagePrice']}
										<small class="error">{fieldErrors['imagePrice']}</small>
									{/if}
								</label>
							</div>

							<h3>长上下文单价</h3>
							<p class="muted">一次请求的总输入超过门槛时，整次（包括输出）都按那一组算。</p>
							{#each groups as group, i (i)}
								<fieldset>
									<legend>第 {i + 1} 组</legend>
									<div class="form-grid">
										<label class="field">
											<span>总输入超过（Token）</span>
											<input
												inputmode="numeric"
												bind:value={group.inputTokensOver}
												aria-invalid={fieldErrors[`longContext[${i}].inputTokensOver`] ? 'true' : undefined}
											/>
											{#if fieldErrors[`longContext[${i}].inputTokensOver`]}
												<small class="error">{fieldErrors[`longContext[${i}].inputTokensOver`]}</small>
											{/if}
										</label>
										{#each priceParts as part (part.key)}
											{@const key = `longContext[${i}].prices.${part.key}`}
											<label class="field">
												<span>{part.label}</span>
												<input
													inputmode="decimal"
													bind:value={group.prices[part.key]}
													aria-invalid={fieldErrors[key] ? 'true' : undefined}
												/>
												{#if fieldErrors[key]}
													<small class="error">{fieldErrors[key]}</small>
												{/if}
											</label>
										{/each}
									</div>
									<button type="button" class="danger" onclick={() => groups.splice(i, 1)}>删掉这一组</button>
								</fieldset>
							{/each}
							<button
								type="button"
								onclick={() => groups.push({ inputTokensOver: '', prices: emptyDraft() })}
								disabled={groups.length >= 10}
							>
								加一组
							</button>

							{#if fieldErrors['longContext']}
								<p class="error">{fieldErrors['longContext']}</p>
							{/if}
							{#if saveError}
								<p class="alert" role="alert">{saveError}</p>
							{/if}
							<div class="actions">
								<button class="primary" type="submit" disabled={saving}>{saving ? '保存中…' : '保存'}</button>
								<button type="button" onclick={cancelEdit}>取消</button>
							</div>
						</form>
					</td>
				</tr>
			{/if}
		{:else}
			<tr>
				<td colspan="15" class="muted">
					{loading ? '加载中…' : filter === 'all' && !search ? '还没有单价行。渠道填了供应商和上游模型后会自动出现。' : '没有符合条件的行。'}
				</td>
			</tr>
		{/each}
	</tbody>
</table>

{#if data && data.total > data.pageSize}
	<nav class="pager" aria-label="翻页">
		<button type="button" onclick={() => go(page - 1)} disabled={page <= 1 || loading}>上一页</button>
		<span>第 {page} / {pageCount} 页，共 {data.total} 行</span>
		<button type="button" onclick={() => go(page + 1)} disabled={page >= pageCount || loading}>下一页</button>
	</nav>
{/if}

<style>
	.facts {
		display: grid;
		grid-template-columns: max-content 1fr;
		gap: 0.25rem 1rem;
	}

	.num {
		text-align: right;
		font-variant-numeric: tabular-nums;
	}

	.small {
		font-size: 0.85em;
	}

	.unused td {
		opacity: 0.6;
	}

	.badge.warn {
		color: var(--warn, #b45309);
	}

	.row-actions {
		display: flex;
		gap: 0.4rem;
		justify-content: flex-end;
	}

	.editor {
		display: grid;
		gap: 0.75rem;
	}

	fieldset {
		display: grid;
		gap: 0.5rem;
		justify-items: start;
	}

	.pager {
		display: flex;
		gap: 1rem;
		align-items: center;
		margin-top: 1rem;
	}
</style>
