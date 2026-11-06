<script lang="ts">
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import {
		apiFormatLabels,
		cooldownScopeLabels,
		failureLabels,
		formatCost,
		formatCount,
		formatTime,
		formatUntil,
		lowHealthThreshold,
		thinkingStyleLabels
	} from '#lib/format.ts';
	import { poll } from '#lib/poll.ts';
	import DebugSwitch from '#lib/DebugSwitch.svelte';
	import RewriteRules from '#lib/RewriteRules.svelte';
	import TimeWindows from '#lib/TimeWindows.svelte';
	import { onMount } from 'svelte';
	import type {
		ApiFormat,
		CatalogView,
		ChannelDetailView,
		ChannelInput,
		ChannelKeyView,
		ChannelRuntimeView,
		CooldownView,
		ModelSyncView,
		TimeWindowView
	} from '#lib/types.ts';

	const formats = Object.keys(apiFormatLabels) as ApiFormat[];
	const formFields = [
		'name',
		'baseUrl',
		'path',
		'apiFormat',
		'provider',
		'priority',
		'weight',
		'multiplier',
		'proxyUrl',
		'upstreamModels',
		'modelSyncEnabled',
		'anthropicBearerAuth',
		'autoCacheBreakpoints',
		'keysShareAccount',
		'thinkingStyle'
	];

	const id = $derived(page.params.id ?? 'new');
	const isNew = $derived(id === 'new');

	let detail = $state<ChannelDetailView | null>(null);
	let loadError = $state<string | null>(null);

	let form = $state<ChannelInput>(blank());
	let modelsText = $state('');
	let saving = $state(false);
	let saveError = $state<ApiError | null>(null);
	let saveNotice = $state<string | null>(null);

	// 出站代理是只写的：已存的值只回显打码后的样子，留空表示不改（Q131）。
	let proxyText = $state('');
	let clearProxy = $state(false);

	let keySecret = $state('');
	let keyName = $state('');
	let keyPriority = $state<number | null>(null);
	let keyError = $state<ApiError | null>(null);
	let keyBusy = $state(false);
	let keyRowError = $state<string | null>(null);

	// 渠道限制（Q77、Q81）。金额按美元填，后端存纳美元。
	let dailyCostLimitUsd = $state<number | null>(null);
	let timeWindows = $state<TimeWindowView[]>([]);
	let limitBusy = $state(false);
	let limitError = $state<ApiError | null>(null);
	let limitNotice = $state<string | null>(null);

	// 运行状态是只读的，10 秒拉一次，页面不可见时停（Q104）。
	let runtime = $state<ChannelRuntimeView | null>(null);

	// 模型同步（Q135、Q140）。lastSync 单独存一份，「立即同步」只换它，不重载整页。
	let lastSync = $state<ModelSyncView | null>(null);
	let syncing = $state(false);
	let syncError = $state<string | null>(null);
	const syncedCount = $derived(
		detail?.upstreamModels.filter((m) => m.source === 'synced').length ?? 0
	);

	// 哪一行的渠道密钥限制正在编辑。
	let editingKey = $state<number | null>(null);
	let keyRpm = $state<number | null>(null);
	let keyConcurrency = $state<number | null>(null);
	let keyModelsText = $state('');
	let keyLimitError = $state<ApiError | null>(null);
	let keyLimitBusy = $state(false);

	function blank(): ChannelInput {
		return {
			name: '',
			baseUrl: '',
			path: null,
			apiFormat: 'openAiChat',
			provider: null,
			priority: 0,
			weight: 1,
			multiplier: 1,
			enabled: true,
			upstreamModels: [],
			modelSyncEnabled: true,
			anthropicBearerAuth: false,
			autoCacheBreakpoints: true,
			keysShareAccount: false,
			thinkingStyle: 'auto'
		};
	}

	function fill(value: ChannelDetailView) {
		detail = value;
		const c = value.channel;
		form = {
			name: c.name,
			baseUrl: c.baseUrl,
			path: c.path,
			apiFormat: c.apiFormat,
			provider: c.provider,
			priority: c.priority,
			weight: c.weight,
			multiplier: c.multiplier,
			enabled: c.enabled,
			upstreamModels: null,
			modelSyncEnabled: c.modelSync?.enabled ?? true,
			anthropicBearerAuth: c.anthropicBearerAuth,
			autoCacheBreakpoints: c.autoCacheBreakpoints,
			keysShareAccount: c.keysShareAccount,
			thinkingStyle: c.thinkingStyle
		};
		lastSync = c.modelSync;
		syncError = null;
		modelsText = value.upstreamModels.map((m) => m.name).join('\n');
		proxyText = '';
		clearProxy = false;
		const limit = value.restrictions.dailyCostLimitNanoUsd;
		dailyCostLimitUsd = limit === null ? null : limit / 1_000_000_000;
		timeWindows = value.restrictions.timeWindows.map((w) => ({ ...w, weekdays: [...w.weekdays] }));
	}

	function startKeyEdit(key: ChannelKeyView) {
		editingKey = key.id;
		keyLimitError = null;
		keyRpm = key.restrictions.requestsPerMinute;
		keyConcurrency = key.restrictions.maxConcurrency;
		keyModelsText = key.restrictions.allowedModels.join('\n');
	}

	function lines(text: string): string[] {
		return text
			.split(/\r?\n/)
			.map((line) => line.trim())
			.filter((line) => line.length > 0);
	}

	async function saveLimits(event: SubmitEvent) {
		event.preventDefault();
		limitBusy = true;
		limitError = null;
		limitNotice = null;
		try {
			await api.put('/api/channels/' + id + '/restrictions', {
				dailyCostLimitUsd,
				timeWindows
			});
			await refresh();
			await loadRuntime();
			limitNotice = '限制已保存。';
		} catch (e) {
			limitError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			limitBusy = false;
		}
	}

	async function saveKeyLimits(event: SubmitEvent) {
		event.preventDefault();
		if (editingKey === null) return;
		keyLimitBusy = true;
		keyLimitError = null;
		try {
			await api.put(`/api/channels/${id}/keys/${editingKey}/restrictions`, {
				requestsPerMinute: keyRpm,
				maxConcurrency: keyConcurrency,
				allowedModels: lines(keyModelsText)
			});
			editingKey = null;
			await refresh();
		} catch (e) {
			keyLimitError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			keyLimitBusy = false;
		}
	}

	async function loadRuntime() {
		if (isNew) return;
		try {
			runtime = await api.get<ChannelRuntimeView>('/api/channels/' + id + '/runtime');
		} catch {
			runtime = null;
		}
	}

	let cooldownBusy = $state(false);
	let cooldownError = $state<string | null>(null);

	/** 手动解除冷却（Q262）：不带参数就是这个渠道的全部。不改健康度。 */
	async function clearCooldowns(cooldown?: CooldownView) {
		const params = new URLSearchParams();
		if (cooldown) {
			params.set('scope', cooldown.scope);
			if (cooldown.keyId !== null) params.set('keyId', String(cooldown.keyId));
			if (cooldown.upstreamModel !== null) params.set('upstreamModel', cooldown.upstreamModel);
		} else if (!confirm('解除这个渠道现在的全部冷却？之后的请求会马上再试它。')) {
			return;
		}
		cooldownBusy = true;
		cooldownError = null;
		try {
			const search = params.toString();
			await api.delete('/api/channels/' + id + '/cooldowns' + (search ? '?' + search : ''));
			await refresh();
		} catch (e) {
			cooldownError = messageOf(e);
		} finally {
			cooldownBusy = false;
		}
	}

	function keyRuntime(keyId: number) {
		return runtime?.keys.find((k) => k.id === keyId);
	}

	// 新建成功后会换到新 id 的地址，effect 随之重跑；提示要跨过这次重置留下来。
	let pendingNotice: string | null = null;

	// 供应商候选来自价目目录（Q71）。取不到就只剩手填，不挡保存。
	let catalog = $state<CatalogView | null>(null);

	onMount(() => {
		api.get<CatalogView>('/api/price-catalog').then(
			(value) => (catalog = value),
			() => (catalog = null)
		);
	});

	$effect(() => {
		if (isNew) return;
		void loadRuntime();
		return poll(loadRuntime, 10_000);
	});

	$effect(() => {
		const current = id;
		detail = null;
		loadError = null;
		saveError = null;
		saveNotice = pendingNotice;
		pendingNotice = null;
		keyError = null;
		if (current === 'new') {
			form = blank();
			modelsText = '';
			return;
		}
		api.get<ChannelDetailView>('/api/channels/' + encodeURIComponent(current)).then(
			(value) => fill(value),
			(e: unknown) => (loadError = messageOf(e))
		);
	});

	function upstreamModels(): string[] {
		return modelsText
			.split(/\r?\n/)
			.map((line) => line.trim())
			.filter((line) => line.length > 0);
	}

	// Provider 必填（Q71）。'' 是明确选了「不在列表里」，null 是还没填。
	function typeProvider(value: string) {
		form.provider = value.trim() === '' ? null : value;
	}

	function markUnlisted(unlisted: boolean) {
		form.provider = unlisted ? '' : null;
	}

	async function save(event: SubmitEvent) {
		event.preventDefault();
		saving = true;
		saveError = null;
		saveNotice = null;
		const body: ChannelInput = {
			...form,
			path: form.path?.trim() ? form.path.trim() : null,
			provider: form.provider === null ? null : form.provider.trim(),
			upstreamModels: upstreamModels(),
			proxyUrl: clearProxy ? '' : proxyText.trim() === '' ? null : proxyText.trim()
		};
		try {
			if (isNew) {
				const created = await api.post<ChannelDetailView>('/api/channels', body);
				pendingNotice = '渠道已创建。接下来给它加一个渠道密钥。';
				await goto(resolve('/channels/[id]', { id: String(created.channel.id) }), {
					replaceState: true
				});
			} else {
				fill(await api.put<ChannelDetailView>('/api/channels/' + id, body));
				saveNotice = '已保存。';
			}
		} catch (e) {
			saveError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			saving = false;
		}
	}

	async function syncNow() {
		syncing = true;
		syncError = null;
		try {
			lastSync = await api.post<ModelSyncView>('/api/channels/' + id + '/sync', {});
			// 同步可能换了清单，重载一次让上游模型和规则命中跟着更新。
			fill(await api.get<ChannelDetailView>('/api/channels/' + id));
		} catch (e) {
			syncError = messageOf(e);
		} finally {
			syncing = false;
		}
	}

	async function remove() {
		if (!detail) return;
		const scoped = detail.scopedRules;
		const warning =
			scoped.length > 0
				? `\n\n这些下发模型里专门指定这个渠道的规则会一起删掉：\n${scoped
						.map((r) => `· ${r.publishedModel} 第 ${r.ordinal} 条`)
						.join('\n')}`
				: '';
		if (!confirm(`删除渠道「${detail.channel.name}」和它的全部渠道密钥？这一步撤销不了。${warning}`)) {
			return;
		}
		try {
			await api.delete('/api/channels/' + id);
			await goto(resolve('/channels'));
		} catch (e) {
			saveError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		}
	}

	async function refresh() {
		fill(await api.get<ChannelDetailView>('/api/channels/' + id));
	}

	async function addKey(event: SubmitEvent) {
		event.preventDefault();
		keyBusy = true;
		keyError = null;
		try {
			await api.post<ChannelKeyView>('/api/channels/' + id + '/keys', {
				secret: keySecret,
				name: keyName.trim() || null,
				priority: keyPriority
			});
			keySecret = '';
			keyName = '';
			keyPriority = null;
			await refresh();
		} catch (e) {
			keyError = e instanceof ApiError ? e : new ApiError(0, { title: messageOf(e) });
		} finally {
			keyBusy = false;
		}
	}

	async function toggleKey(key: ChannelKeyView) {
		keyRowError = null;
		try {
			await api.patch(`/api/channels/${id}/keys/${key.id}`, { enabled: !key.enabled });
			await refresh();
		} catch (e) {
			keyRowError = messageOf(e);
		}
	}

	async function removeKey(key: ChannelKeyView) {
		if (!confirm(`删除渠道密钥「${key.name}」（${key.prefix}…）？`)) return;
		keyRowError = null;
		try {
			await api.delete(`/api/channels/${id}/keys/${key.id}`);
			await refresh();
		} catch (e) {
			keyRowError = messageOf(e);
		}
	}
</script>

<svelte:head>
	<title>{isNew ? '新建渠道' : (detail?.channel.name ?? '渠道')} · 南天门</title>
</svelte:head>

<p><a href={resolve('/channels')}>← 返回渠道列表</a></p>
<h1>{isNew ? '新建渠道' : (detail?.channel.name ?? '渠道')}</h1>

{#if loadError}
	<p class="alert" role="alert">{loadError}</p>
{:else if !isNew && !detail}
	<p class="muted">加载中…</p>
{:else}
	{#if !isNew && detail}
		<p>
			<a href={resolve('/workbench') + '?channel=' + detail.channel.id}>在工作台里测试这个渠道</a>
			<span class="muted">（停用的渠道和密钥也能测；没保存的改动不会生效）</span>
			· <a href={resolve('/requests') + '?channelId=' + detail.channel.id}>看它的请求</a>
		</p>
	{/if}
	{#if saveNotice}
		<p class="notice" role="status">{saveNotice}</p>
	{/if}
	{#if saveError}
		<p class="alert" role="alert">{saveError.summary(formFields)}</p>
	{/if}

	<form class="panel" onsubmit={save} novalidate>
		<div class="form-grid">
			<label class="field">
				<span>名字</span>
				<input bind:value={form.name} maxlength="200" required />
				{#if saveError?.field('name')}<span class="error">{saveError.field('name')}</span>{/if}
			</label>
			<label class="field">
				<span>API 格式</span>
				<select bind:value={form.apiFormat}>
					{#each formats as format (format)}
						<option value={format}>{apiFormatLabels[format]}</option>
					{/each}
				</select>
				{#if saveError?.field('apiFormat')}<span class="error">{saveError.field('apiFormat')}</span>{/if}
			</label>
			{#if form.apiFormat === 'anthropic'}
				<div class="field">
					<span>认证头</span>
					<label class="check">
						<input type="checkbox" bind:checked={form.anthropicBearerAuth} />
						<span>用 Authorization: Bearer 带密钥</span>
					</label>
					<!-- 一行写完：Svelte 会把源码里的换行折成一个空格，中文中间多出空格。 -->
					<span class="hint">默认放在 x-api-key 头里；有的中转只认 Bearer，就勾上。两个头不会同时带。</span>
				</div>
				<div class="field">
					<span>提示缓存</span>
					<label class="check">
						<input type="checkbox" bind:checked={form.autoCacheBreakpoints} />
						<span>自动加缓存断点</span>
					</label>
					<span class="hint">OpenAI 格式的请求转到这个渠道时，客户端没写 cache_control 就由网关加（最多 4 个，5 分钟），省下重复输入的钱。Claude 客户端直连的请求不动。中转拒绝带断点的请求时关掉。</span>
				</div>
				<div class="field">
					<label for="channel-thinking">思考参数写法</label>
					<select id="channel-thinking" bind:value={form.thinkingStyle}>
						{#each Object.entries(thinkingStyleLabels) as [value, label] (value)}
							<option {value}>{label}</option>
						{/each}
					</select>
					<span class="hint">OpenAI 格式的请求带推理强度转到这个渠道时怎么写 thinking。自动：模型名里 Claude 4.6 及以后用自适应（adaptive 加 effort），其余（包括第三方模型）用预算（enabled 加 budget_tokens）。上游报「thinking.type.enabled is not supported」或「adaptive thinking is not supported」时改成固定写法。</span>
				</div>
			{/if}
			<div class="field">
				<span>渠道密钥</span>
				<label class="check">
					<input type="checkbox" bind:checked={form.keysShareAccount} />
					<span>密钥属于同一个账号</span>
				</label>
				<span class="hint">只影响 Responses 格式的渠道。勾上后，previous_response_id、conversation 和按 id 取响应在产出它的那把密钥用不了时，可以改用这个渠道的别的密钥（同一账号的密钥能看到彼此的响应）。密钥分属不同账号时别勾，换了密钥上游会说找不到。</span>
			</div>
			<div class="field">
				<label for="channel-provider">供应商</label>
				<input
					id="channel-provider"
					value={form.provider ?? ''}
					oninput={(event) => typeProvider(event.currentTarget.value)}
					disabled={form.provider === ''}
					maxlength="100"
					autocomplete="off"
					placeholder="例如 anthropic、openai、openrouter"
					list="provider-options"
				/>
				<datalist id="provider-options">
					{#each catalog?.providers ?? [] as provider (provider.id)}
						<option value={provider.id}>{provider.name}（{provider.modelCount} 个模型）</option>
					{/each}
				</datalist>
				<label class="check">
					<input
						type="checkbox"
						checked={form.provider === ''}
						onchange={(event) => markUnlisted(event.currentTarget.checked)}
					/>
					不在列表里（没有自动单价）
				</label>
				<span class="hint">
					按谁的价目表计价。填中转背后的原厂时，差价交给倍率。
					{#if catalog && catalog.providers.length > 0}
						目录里有 {catalog.providers.length} 个供应商可选。
					{:else if catalog}
						还没取到价目目录，先手填，之后在「单价」页刷新。
					{/if}
				</span>
				{#if saveError?.field('provider')}<span class="error">{saveError.field('provider')}</span>{/if}
			</div>
			<label class="field wide">
				<span>上游地址</span>
				<input
					bind:value={form.baseUrl}
					type="url"
					placeholder="https://api.example.com/v1"
					required
				/>
				<span class="hint">末尾的 /v1 这类版本段会自动拆出来，不会拼成 /v1/v1。</span>
				{#if saveError?.field('baseUrl')}<span class="error">{saveError.field('baseUrl')}</span>{/if}
			</label>
			<label class="field wide">
				<span>请求路径（可选）</span>
				<input bind:value={form.path} placeholder="留空按 API 格式默认" />
				{#if detail?.channel.requestUrl}
					<span class="hint">实际请求：<code>{detail.channel.requestUrl}</code></span>
				{/if}
				{#if saveError?.field('path')}<span class="error">{saveError.field('path')}</span>{/if}
			</label>
			<label class="field">
				<span>优先级</span>
				<input type="number" bind:value={form.priority} min="-1000" max="1000" step="1" />
				<span class="hint">数字大的先用，同档内按权重分流。</span>
				{#if saveError?.field('priority')}<span class="error">{saveError.field('priority')}</span>{/if}
			</label>
			<label class="field">
				<span>权重</span>
				<input type="number" bind:value={form.weight} min="1" max="1000" step="1" />
				{#if saveError?.field('weight')}<span class="error">{saveError.field('weight')}</span>{/if}
			</label>
			<label class="field">
				<span>计费倍率</span>
				<input type="number" bind:value={form.multiplier} min="0.0001" max="100" step="0.0001" />
				{#if saveError?.field('multiplier')}<span class="error">{saveError.field('multiplier')}</span>{/if}
			</label>
			<label class="check">
				<input type="checkbox" bind:checked={form.enabled} />
				<span>启用</span>
			</label>
		</div>

		<div class="field proxy">
			<label for="channel-proxy">出站代理</label>
			{#if detail?.channel.proxy}
				<p class="current">
					{#if detail.channel.proxy.readable}
						现在经 <code>{detail.channel.proxy.masked}</code> 连接上游。
					{:else}
						<span class="error">存着的代理地址解不开（密钥环换过？），这个渠道暂时不参与选路。请重新填写或清除。</span>
					{/if}
				</p>
			{/if}
			<input
				id="channel-proxy"
				bind:value={proxyText}
				disabled={clearProxy}
				placeholder={detail?.channel.proxy ? '留空表示不改' : '留空表示直连，例如 socks5://user:pass@127.0.0.1:1080'}
				autocomplete="off"
				spellcheck="false"
			/>
			{#if detail?.channel.proxy}
				<label class="check">
					<input type="checkbox" bind:checked={clearProxy} />
					<span>清除代理，改为直连</span>
				</label>
			{/if}
			<span class="hint">
				支持 http、https、socks4、socks4a、socks5、socks5h。socks5 和 socks5h 都由代理解析域名。地址加密保存，保存后只显示打码的样子。
			</span>
			{#if saveError?.field('proxyUrl')}<span class="error">{saveError.field('proxyUrl')}</span>{/if}
		</div>

		<label class="field models">
			<span>上游模型（每行一个）</span>
			<textarea bind:value={modelsText} rows="8" spellcheck="false"></textarea>
			<span class="hint">
				这个渠道能服务的模型名，下发模型的匹配规则从这里挑。
				{#if syncedCount > 0}
					其中 {syncedCount} 个是同步来的，手动删掉下次同步还会回来；不想让它参与选路，去下发模型里加一条排除规则。
				{/if}
			</span>
			{#if saveError?.field('upstreamModels')}
				<span class="error">{saveError.field('upstreamModels')}</span>
			{/if}
		</label>

		<div class="field sync">
			<label class="check">
				<input type="checkbox" bind:checked={form.modelSyncEnabled} />
				<span>自动同步上游模型清单</span>
			</label>
			<span class="hint">
				每小时一次，用这个渠道的密钥去上游列模型，全量替换同步来的条目，人工填的不动。它从不创建、也从不启用下发模型。
			</span>
			{#if detail}
				<div class="sync-run">
					<button type="button" onclick={syncNow} disabled={syncing}>
						{syncing ? '同步中…' : '立即同步'}
					</button>
					<span class="hint">这个按钮也是密钥和代理的连通性测试，开关关着也能点。</span>
				</div>
				{#if syncError}
					<p class="error" role="alert">{syncError}</p>
				{:else if lastSync?.lastAt}
					<p class={lastSync.lastSucceeded ? 'muted' : 'error'}>
						{formatTime(lastSync.lastAt)}：{lastSync.lastMessage}
					</p>
				{:else}
					<p class="muted">还没同步过。</p>
				{/if}
				<p class="hint">
					模型进出选路会记在<a href={resolve('/model-sync-events')}>模型同步事件</a>里。
				</p>
			{/if}
		</div>

		<div class="actions">
			<button class="primary" type="submit" disabled={saving}>
				{saving ? '保存中…' : isNew ? '创建渠道' : '保存'}
			</button>
			{#if !isNew}
				<button type="button" class="danger" onclick={remove}>删除渠道</button>
			{/if}
		</div>
	</form>

	{#if detail}
		<h2>限制</h2>
		<p class="muted">
			触发限制时这个渠道暂时退出选路，不进入冷却。日费用按网关时区（{runtime?.timeZone ?? '…'}）的自然日算，只在请求开始前检查，所以实际花费可能略超上限。
		</p>
		{#if runtime}
			<div class="runtime">
				<span>
					今日已花 <strong>{formatCost(runtime.spentTodayNanoUsd)}</strong>
					{#if runtime.dailyCostLimitNanoUsd !== null}
						/ {formatCost(runtime.dailyCostLimitNanoUsd)}
						{#if runtime.spentTodayNanoUsd >= runtime.dailyCostLimitNanoUsd}
							<span class="badge error">已达上限</span>
						{/if}
					{:else}
						<span class="muted">（不限）</span>
					{/if}
				</span>
				<span>进行中 <strong>{formatCount(runtime.inFlight)}</strong></span>
				{#if !runtime.inTimeWindow}
					<span class="badge error">
						不在可用时段{runtime.nextWindowStart ? `，${formatUntil(runtime.nextWindowStart)}开放` : ''}
					</span>
				{/if}
				{#if runtime.unpricedTodayCount > 0}
					<span class="badge warn" title="没有单价的请求按 0 计入上限">
						今日 {formatCount(runtime.unpricedTodayCount)} 个请求无单价，没算进上限
					</span>
				{/if}
			</div>
		{/if}

		<form class="panel" onsubmit={saveLimits} novalidate>
			<div class="form-grid">
			<label class="field">
				<span>每日费用上限（美元，可选）</span>
				<input type="number" bind:value={dailyCostLimitUsd} min="0.000001" step="0.000001" placeholder="不限" />
				<span class="hint">按倍率后的实际费用算。留空表示不限。</span>
				{#if limitError?.field('dailyCostLimitUsd')}
					<span class="error">{limitError.field('dailyCostLimitUsd')}</span>
				{/if}
			</label>
			</div>
			<h3>可用时段</h3>
			<TimeWindows bind:windows={timeWindows} error={limitError} />
			<div class="actions">
				<button class="primary" type="submit" disabled={limitBusy}>{limitBusy ? '保存中…' : '保存限制'}</button>
				{#if limitNotice}<span class="ok" role="status">{limitNotice}</span>{/if}
			</div>
			{#if limitError && Object.keys(limitError.fields).length === 0}
				<p class="alert" role="alert">{limitError.message}</p>
			{/if}
		</form>

		<h2>请求改写</h2>
		<RewriteRules
			channelId={detail.channel.id}
			rules={detail.rewriteRules}
			onSaved={(rules) => {
				if (detail) detail = { ...detail, rewriteRules: rules };
			}}
		/>

		<h2>调试原文</h2>
		<div class="panel">
			<DebugSwitch url={`/api/channels/${detail.channel.id}/debug-capture`} label="这个渠道" />
		</div>

		<h2>渠道密钥</h2>
		{#if keyRowError}
			<p class="alert" role="alert">{keyRowError}</p>
		{/if}
		<table>
			<thead>
				<tr>
					<th scope="col">名字</th>
					<th scope="col">前缀</th>
					<th scope="col" class="num">优先级</th>
					<th scope="col">状态</th>
					<th scope="col">限制</th>
					<th scope="col" class="num" title="本分钟已用 / RPM 上限">本分钟</th>
					<th scope="col" class="num">进行中</th>
					<th scope="col">添加时间</th>
					<th scope="col"><span class="visually-hidden">操作</span></th>
				</tr>
			</thead>
			<tbody>
				{#each detail.keys as key (key.id)}
					<tr>
						<td>{key.name}</td>
						<td class="mono">{key.prefix}…</td>
						<td class="num">{key.priority}</td>
						<td>
							<span class={key.enabled ? 'ok' : 'muted'}>{key.enabled ? '启用' : '已停用'}</span>
							{#if !key.readable}
								<span class="badge error" title="当前密钥环解不开它，选路时会跳过">解不开</span>
							{/if}
						</td>
						<td>
							<div class="limits">
							{#if key.restrictions.requestsPerMinute !== null}
								<span>RPM {key.restrictions.requestsPerMinute}</span>
							{/if}
							{#if key.restrictions.maxConcurrency !== null}
								<span>并发 {key.restrictions.maxConcurrency}</span>
							{/if}
							{#if key.restrictions.allowedModels.length > 0}
								<span title={key.restrictions.allowedModels.join('\n')}>
									只用于 {key.restrictions.allowedModels.length} 个模型
								</span>
							{/if}
							{#if key.restrictions.requestsPerMinute === null && key.restrictions.maxConcurrency === null && key.restrictions.allowedModels.length === 0}
								<span class="muted">不限</span>
							{/if}
							</div>
						</td>
						<td class="num">
							{formatCount(keyRuntime(key.id)?.requestsThisMinute ?? 0)}{key.restrictions.requestsPerMinute !== null
								? ` / ${key.restrictions.requestsPerMinute}`
								: ''}
						</td>
						<td class="num">
							{formatCount(keyRuntime(key.id)?.inFlight ?? 0)}{key.restrictions.maxConcurrency !== null
								? ` / ${key.restrictions.maxConcurrency}`
								: ''}
						</td>
						<td>{formatTime(key.createdAt)}</td>
						<td class="row-actions">
							<button type="button" onclick={() => startKeyEdit(key)}>限制</button>
							<button type="button" onclick={() => toggleKey(key)}>{key.enabled ? '停用' : '启用'}</button>
							<button
								type="button"
								class="danger"
								onclick={() => removeKey(key)}
								aria-label={`删除渠道密钥 ${key.name}`}
							>
								删除
							</button>
						</td>
					</tr>
					{#if editingKey === key.id}
						<tr>
							<td colspan="9">
								<form class="key-limits" onsubmit={saveKeyLimits} novalidate>
									<label class="field">
										<span>RPM 上限（可选）</span>
										<input type="number" bind:value={keyRpm} min="1" step="1" placeholder="不限" />
										{#if keyLimitError?.field('requestsPerMinute')}
											<span class="error">{keyLimitError.field('requestsPerMinute')}</span>
										{/if}
									</label>
									<label class="field">
										<span>并发上限（可选）</span>
										<input type="number" bind:value={keyConcurrency} min="1" step="1" placeholder="不限" />
										{#if keyLimitError?.field('maxConcurrency')}
											<span class="error">{keyLimitError.field('maxConcurrency')}</span>
										{/if}
									</label>
									<label class="field models">
										<span>只用于这些上游模型（每行一个，留空表示全部）</span>
										<textarea bind:value={keyModelsText} rows="4" spellcheck="false"></textarea>
										<span class="hint">按名字精确匹配。不在名单里的模型不会用这把渠道密钥，也不算失败。</span>
										{#if keyLimitError?.field('allowedModels')}
											<span class="error">{keyLimitError.field('allowedModels')}</span>
										{/if}
									</label>
									<div class="actions">
										<button class="primary" type="submit" disabled={keyLimitBusy}>
											{keyLimitBusy ? '保存中…' : '保存'}
										</button>
										<button type="button" onclick={() => (editingKey = null)}>取消</button>
									</div>
									{#if keyLimitError && Object.keys(keyLimitError.fields).length === 0}
										<p class="alert" role="alert">{keyLimitError.message}</p>
									{/if}
								</form>
							</td>
						</tr>
					{/if}
				{:else}
					<tr><td colspan="9" class="muted">还没有渠道密钥，这个渠道暂时不会被选中。</td></tr>
				{/each}
			</tbody>
		</table>

		<form class="toolbar add-key" onsubmit={addKey} novalidate>
			<label class="field secret">
				<span>新渠道密钥</span>
				<input
					type="password"
					autocomplete="off"
					bind:value={keySecret}
					placeholder="只写不读，保存后只显示前缀"
				/>
				{#if keyError?.field('secret')}<span class="error">{keyError.field('secret')}</span>{/if}
			</label>
			<label class="field">
				<span>名字（可选）</span>
				<input bind:value={keyName} maxlength="200" />
				{#if keyError?.field('name')}<span class="error">{keyError.field('name')}</span>{/if}
			</label>
			<label class="field">
				<span>优先级（可选）</span>
				<input type="number" bind:value={keyPriority} min="-1000" max="1000" step="1" />
				{#if keyError?.field('priority')}<span class="error">{keyError.field('priority')}</span>{/if}
			</label>
			<button class="primary" type="submit" disabled={keyBusy || keySecret.trim().length === 0}>
				{keyBusy ? '添加中…' : '添加渠道密钥'}
			</button>
		</form>
		{#if keyError && !keyError.field('secret') && !keyError.field('name') && !keyError.field('priority')}
			<p class="alert" role="alert">{keyError.message}</p>
		{/if}

		<h2>冷却</h2>
		{#if cooldownError}
			<p class="alert" role="alert">{cooldownError}</p>
		{/if}
		{#if detail.cooldowns.length === 0}
			<p class="muted">现在没有冷却。</p>
		{:else}
			<p class="muted hint">
				解除后下一个请求会马上再试；密钥维度的冷却按密钥值记，同一份密钥挂在别的渠道上时那边也一起解除。解除不改健康度。
			</p>
			<table>
				<thead>
					<tr>
						<th scope="col">范围</th>
						<th scope="col">渠道密钥</th>
						<th scope="col">上游模型</th>
						<th scope="col">原因</th>
						<th scope="col">到期</th>
						<th scope="col">
							<button type="button" onclick={() => clearCooldowns()} disabled={cooldownBusy}>全部解除</button>
						</th>
					</tr>
				</thead>
				<tbody>
					{#each detail.cooldowns as cooldown, i (i)}
						<tr>
							<td>{cooldownScopeLabels[cooldown.scope]}</td>
							<td class="mono">{cooldown.keyPrefix ? cooldown.keyPrefix + '…' : '—'}</td>
							<td class="mono">{cooldown.upstreamModel ?? '—'}</td>
							<td>{failureLabels[cooldown.failure]}</td>
							<td>{formatUntil(cooldown.until)} <span class="muted">（{formatTime(cooldown.until)}）</span></td>
							<td>
								<button type="button" onclick={() => clearCooldowns(cooldown)} disabled={cooldownBusy}>解除</button>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		{/if}

		<h2>健康度</h2>
		<p class="muted hint">
			近 30 分钟每个上游模型计入健康度的成功、失败次数。只算渠道本身的问题（5xx、超时、连不上、软错误、模型不存在等）；
			认证失败、额度、限流和客户端自己的错误不算，它们由冷却处理。系数在 0.1 到 1 之间，样本不到 20 个时往 1 靠，
			只在同一个优先级档里按比例调低权重。只在内存里，网关重启后从零算。
		</p>
		{#if runtime && runtime.health.length > 0}
			<table>
				<thead>
					<tr>
						<th scope="col">上游模型</th>
						<th scope="col" class="num">成功</th>
						<th scope="col" class="num">失败</th>
						<th scope="col" class="num">系数</th>
					</tr>
				</thead>
				<tbody>
					{#each runtime.health as sample (sample.upstreamModel)}
						<tr>
							<td class="mono">{sample.upstreamModel}</td>
							<td class="num">{formatCount(sample.successes)}</td>
							<td class="num">{formatCount(sample.failures)}</td>
							<td class="num" class:warn={sample.factor < lowHealthThreshold}>{sample.factor.toFixed(2)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		{:else}
			<p class="muted">近 30 分钟没有计入健康度的请求。</p>
		{/if}

		{#if detail.scopedRules.length > 0}
			<h2>专门指定这个渠道的规则</h2>
			<ul>
				{#each detail.scopedRules as rule (rule.publishedModelId + ':' + rule.ordinal)}
					<li>
						<a href={resolve('/models/[id]', { id: String(rule.publishedModelId) })}>{rule.publishedModel}</a>
						第 {rule.ordinal} 条
					</li>
				{/each}
			</ul>
		{/if}
	{/if}
{/if}

<style>
	.wide {
		grid-column: span 2;
	}

	.models {
		margin-top: 1rem;
	}

	.proxy,
	.sync {
		margin-top: 1rem;
	}

	.sync-run {
		display: flex;
		align-items: center;
		gap: 0.75rem;
		flex-wrap: wrap;
	}

	.sync p {
		margin: 0.25rem 0 0;
	}

	.proxy .current {
		margin: 0;
	}

	.add-key {
		margin-top: 0.75rem;
	}

	.hint {
		font-size: 0.85rem;
	}

	.runtime {
		display: flex;
		flex-wrap: wrap;
		gap: 1rem;
		align-items: center;
		margin-bottom: 0.75rem;
	}

	.limits {
		display: flex;
		flex-wrap: wrap;
		gap: 0.4rem;
		font-size: 0.9rem;
	}

	.key-limits {
		display: flex;
		flex-wrap: wrap;
		gap: 0.75rem;
		align-items: flex-start;
	}

	.key-limits .models {
		flex: 1;
		min-width: 18rem;
		margin-top: 0;
	}

	.secret {
		flex: 1;
		min-width: 16rem;
	}

	.row-actions {
		display: flex;
		gap: 0.4rem;
		justify-content: flex-end;
	}

	@media (max-width: 40rem) {
		.wide {
			grid-column: auto;
		}
	}
</style>
