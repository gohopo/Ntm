<script lang="ts">
	// 工作台（Q199–Q216）：渠道测试、模型测试、对话。
	// 请求体由前端按目标格式拼（Q202），发到 /api/workbench/…；响应是客户端会收到的那份字节。
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, messageOf } from '#lib/api.ts';
	import {
		apiFormatLabels,
		cooldownScopeLabels,
		formatCost,
		formatCount,
		formatMs,
		formatUntil,
		failureLabels,
		requestKindLabels
	} from '#lib/format.ts';
	import WorkbenchReply from '#lib/WorkbenchReply.svelte';
	import {
		buildBody,
		buildKindBody,
		effortLabels,
		emptyReply,
		fetchRecord,
		imageSizes,
		loadSettings,
		readImage,
		saveSettings,
		sendWorkbench,
		type Effort,
		type ImageAttachment,
		type Reply,
		type Turn,
		type WorkbenchKind,
		type WorkbenchSettings
	} from '#lib/workbench.ts';
	import type { ApiFormat, ChannelDetailView, ChannelView, ModelDetailView, ModelView, RequestDetailView } from '#lib/types.ts';

	type Tab = 'channel' | 'model' | 'chat';
	const tabs: { id: Tab; label: string }[] = [
		{ id: 'channel', label: '渠道测试' },
		{ id: 'model', label: '模型测试' },
		{ id: 'chat', label: '对话' }
	];
	const formats: ApiFormat[] = ['openAiChat', 'openAiResponses', 'anthropic'];
	const efforts: Effort[] = ['off', 'low', 'medium', 'high'];

	let tab = $state<Tab>('channel');
	let settings = $state<WorkbenchSettings>(loadSettings());
	$effect(() => saveSettings($state.snapshot(settings)));

	let channels = $state<ChannelView[]>([]);
	let models = $state<ModelView[]>([]);
	// 对话页只列对话模型；模型测试三类都能测（Q238）。
	const chatModels = $derived(models.filter((model) => model.requestKind === 'chat'));
	const workbenchKinds: WorkbenchKind[] = ['chat', 'embedding', 'image'];
	const imageCostNotice = '图片生成按张收费，一次测试就是一张真图的钱。';
	let loadError = $state<string | null>(null);

	onMount(() => {
		const wanted = Number(page.url.searchParams.get('channel'));
		Promise.all([api.get<ChannelView[]>('/api/channels'), api.get<ModelView[]>('/api/models')]).then(
			([c, m]) => {
				channels = c;
				models = m.filter((model) => (workbenchKinds as string[]).includes(model.requestKind));
				if (wanted && c.some((channel) => channel.id === wanted)) {
					tab = 'channel';
					void pickChannel(wanted);
				}
			},
			(e) => (loadError = messageOf(e))
		);
	});

	// ——— 渠道测试（Q200、Q209、Q210、Q216） ———

	let channelId = $state<number | ''>('');
	let detail = $state<ChannelDetailView | null>(null);
	let keyId = $state<number | ''>('');
	let chosenModels = $state<string[]>([]);
	let extraModel = $state('');
	let channelRunning = $state(false);
	let channelAbort: AbortController | null = null;

	interface TestRow {
		model: string;
		state: 'waiting' | 'running' | 'done';
		requestId: string | null;
		reply: Reply;
		record: RequestDetailView | null;
	}
	let rows = $state<TestRow[]>([]);

	const channel = $derived(detail?.channel ?? null);
	// 嵌入和图片只能测 OpenAI Chat 格式的渠道（Q221）。
	const channelKind = $derived<WorkbenchKind>(channel?.apiFormat === 'openAiChat' ? settings.channelKind : 'chat');
	const keyPrefix = $derived(detail?.keys.find((k) => k.id === keyId)?.prefix ?? null);
	const relevantCooldowns = $derived(
		(detail?.cooldowns ?? []).filter(
			(c) =>
				(c.keyPrefix === null || keyPrefix === null || c.keyPrefix === keyPrefix) &&
				(c.upstreamModel === null || chosenModels.length === 0 || chosenModels.includes(c.upstreamModel))
		)
	);

	async function pickChannel(id: number | '') {
		channelId = id;
		detail = null;
		keyId = '';
		chosenModels = [];
		rows = [];
		if (id === '') return;
		try {
			detail = await api.get<ChannelDetailView>('/api/channels/' + id);
			chosenModels = [rememberedModel(id, detail.upstreamModels.map((m) => m.name))].filter((m) => m !== '');
		} catch (e) {
			loadError = messageOf(e);
		}
	}

	/** 上次在这个渠道上用的上游模型；没有记录时用字母序第一个（Q265）。 */
	function rememberedModel(id: number, names: string[]): string {
		const last = settings.lastModels[String(id)];
		return last && names.includes(last) ? last : (names[0] ?? '');
	}

	function rememberModel(id: number | '', model: string) {
		if (id === '' || !model) return;
		settings.lastModels[String(id)] = model;
	}

	function toggleModel(name: string) {
		chosenModels = chosenModels.includes(name) ? chosenModels.filter((m) => m !== name) : [...chosenModels, name];
	}

	function addExtraModel(event: SubmitEvent) {
		event.preventDefault();
		const name = extraModel.trim();
		if (name && !chosenModels.includes(name)) chosenModels = [...chosenModels, name];
		extraModel = '';
	}

	function channelPath(id: number, key: number | '', kind: WorkbenchKind): string {
		const params = new URLSearchParams();
		if (key !== '') params.set('key', String(key));
		if (kind !== 'chat') params.set('kind', kind);
		const query = params.toString();
		return `/api/workbench/channels/${id}/send` + (query ? '?' + query : '');
	}

	/** 嵌入和图片的测试请求体（Q238）：图片不开流式，n=1。 */
	function kindBody(kind: Exclude<WorkbenchKind, 'chat'>, model: string): Record<string, unknown> {
		return buildKindBody(kind, {
			model,
			input: settings.embeddingInput,
			prompt: settings.imagePrompt,
			size: settings.imageSize,
			stream: false
		});
	}

	async function runChannelTest() {
		if (!channel || chosenModels.length === 0) return;
		channelRunning = true;
		channelAbort = new AbortController();
		const format = channel.apiFormat;
		const kind = channelKind;
		rememberModel(channel.id, chosenModels[0]);
		rows = chosenModels.map((model) => ({ model, state: 'waiting', requestId: null, reply: emptyReply(), record: null }));
		// 一个一个测，前一个失败不影响下一个（Q216）。
		for (const row of rows) {
			if (channelAbort.signal.aborted) break;
			row.state = 'running';
			const body =
				kind === 'chat'
					? buildBody(format, {
							model: row.model,
							turns: [{ role: 'user', text: settings.channelPrompt, images: [] }],
							stream: true,
							effort: 'off',
							search: false,
							imageGeneration: false
						})
					: kindBody(kind, row.model);
			const result = await sendWorkbench(
				channelPath(channel.id, keyId, kind),
				format,
				JSON.stringify(body),
				channelAbort.signal,
				(reply) => (row.reply = { ...reply }),
				kind
			);
			row.reply = { ...result.reply };
			row.requestId = result.requestId;
			row.state = 'done';
			if (result.requestId) row.record = await fetchRecord(result.requestId);
		}
		channelRunning = false;
		// 成功的测试清了冷却（Q210），刷新一下看得到。
		if (channelId !== '') {
			try {
				detail = await api.get<ChannelDetailView>('/api/channels/' + channelId);
			} catch {
				// 刷新失败不影响结果显示。
			}
		}
	}

	// ——— 模型测试（Q200、Q203） ———

	let modelName = $state('');
	let modelDetail = $state<ModelDetailView | null>(null);
	let modelRun = $state<Exchange | null>(null);
	let modelAbort: AbortController | null = null;

	const channelFormats = $derived(new Map(channels.map((c) => [c.id, c.apiFormat])));
	const modelKind = $derived<WorkbenchKind>(
		(models.find((m) => m.name === modelName)?.requestKind as WorkbenchKind | undefined) ?? 'chat'
	);

	async function pickModel(name: string) {
		modelName = name;
		modelDetail = null;
		const model = models.find((m) => m.name === name);
		if (!model) return;
		try {
			modelDetail = await api.get<ModelDetailView>('/api/models/' + model.id);
		} catch (e) {
			loadError = messageOf(e);
		}
	}

	async function runModelTest() {
		if (!modelName) return;
		modelAbort = new AbortController();
		const kind = modelKind;
		if (kind !== 'chat') {
			// 嵌入和图片只有 OpenAI 形状，入站格式不用选（Q221）。
			modelRun = newExchange(JSON.stringify(kindBody(kind, modelName), null, 2));
			await deliver(modelRun, `/api/workbench/models/send?kind=${kind}`, 'openAiChat', modelAbort, kind);
			return;
		}
		const body = buildBody(settings.modelFormat, {
			model: modelName,
			turns: [{ role: 'user', text: settings.modelPrompt, images: [] }],
			stream: true,
			effort: 'off',
			search: false,
			imageGeneration: false
		});
		modelRun = newExchange(JSON.stringify(body, null, 2));
		await deliver(modelRun, `/api/workbench/models/send?format=${settings.modelFormat}`, settings.modelFormat, modelAbort);
	}

	// ——— 对话（Q200、Q203–Q207、Q211–Q214） ———

	type ChatTarget = 'channel' | 'model';
	let chatTarget = $state<ChatTarget>('model');
	let chatChannelId = $state<number | ''>('');
	let chatChannelModels = $state<string[]>([]);
	let chatUpstreamModel = $state('');
	let chatModel = $state('');
	let draft = $state('');
	let attachments = $state<ImageAttachment[]>([]);
	let attachError = $state<string | null>(null);
	let editRaw = $state(false);
	let rawDraft = $state('');
	let rawError = $state<string | null>(null);

	interface Exchange {
		requestBody: string;
		running: boolean;
		requestId: string | null;
		reply: Reply;
		raw: string;
		record: RequestDetailView | null;
		loadingRecord: boolean;
		stopped: boolean;
	}
	interface ChatMessage {
		turn: Turn;
		exchange: Exchange | null;
	}
	let messages = $state<ChatMessage[]>([]);
	let chatAbort: AbortController | null = null;
	const chatRunning = $derived(messages.some((m) => m.exchange?.running));

	const chatChannel = $derived(channels.find((c) => c.id === chatChannelId) ?? null);
	/** 以渠道为目标时用渠道自己的格式透传；以下发模型为目标时由 Admin 选（Q203）。 */
	const chatFormat = $derived<ApiFormat>(chatTarget === 'channel' ? (chatChannel?.apiFormat ?? 'openAiChat') : settings.modelFormat);
	const chatReady = $derived(chatTarget === 'channel' ? chatChannel !== null && chatUpstreamModel.trim() !== '' : chatModel !== '');
	const imageGenerationAvailable = $derived(chatFormat !== 'anthropic');

	async function pickChatChannel(id: number | '') {
		chatChannelId = id;
		chatChannelModels = [];
		if (id === '') return;
		try {
			const d = await api.get<ChannelDetailView>('/api/channels/' + id);
			chatChannelModels = d.upstreamModels.map((m) => m.name);
			chatUpstreamModel = rememberedModel(id, chatChannelModels);
		} catch (e) {
			loadError = messageOf(e);
		}
	}

	function chatTurns(next: Turn): Turn[] {
		// 失败或停掉的回复不进历史：把一条用户消息后面跟空回复发给上游没有意义。
		const turns: Turn[] = [];
		for (const message of messages) {
			if (message.turn.role === 'assistant' && !message.turn.text) continue;
			turns.push(message.turn);
		}
		turns.push(next);
		return turns;
	}

	function draftBody(): Record<string, unknown> {
		return buildBody(chatFormat, {
			model: chatTarget === 'channel' ? chatUpstreamModel.trim() : chatModel,
			turns: chatTurns({ role: 'user', text: draft, images: attachments }),
			stream: settings.stream,
			effort: settings.effort,
			search: settings.search,
			imageGeneration: settings.imageGeneration && imageGenerationAvailable,
			thinkingStyle: chatTarget === 'channel' ? chatChannel?.thinkingStyle : undefined
		});
	}

	function openRawEditor() {
		editRaw = !editRaw;
		rawError = null;
		if (editRaw) rawDraft = JSON.stringify(draftBody(), null, 2);
	}

	async function sendChat(event: SubmitEvent) {
		event.preventDefault();
		if (!chatReady || chatRunning || (!draft.trim() && attachments.length === 0 && !editRaw)) return;
		if (chatTarget === 'channel') rememberModel(chatChannelId, chatUpstreamModel.trim());

		let body: string;
		if (editRaw) {
			try {
				body = JSON.stringify(JSON.parse(rawDraft), null, 2);
			} catch {
				rawError = '原始 JSON 不是合法的 JSON。';
				return;
			}
		} else {
			body = JSON.stringify(draftBody(), null, 2);
		}

		const exchange = newExchange(body);
		messages.push({ turn: { role: 'user', text: draft, images: attachments }, exchange: null });
		messages.push({ turn: { role: 'assistant', text: '', images: [] }, exchange });
		const index = messages.length - 1;
		draft = '';
		attachments = [];
		editRaw = false;

		chatAbort = new AbortController();
		const path =
			chatTarget === 'channel' && chatChannel
				? `/api/workbench/channels/${chatChannel.id}/send`
				: `/api/workbench/models/send?format=${chatFormat}`;
		const live = messages[index].exchange!;
		await deliver(live, path, chatFormat, chatAbort);
		messages[index].turn.text = live.reply.text;
	}

	function clearChat() {
		chatAbort?.abort();
		messages = [];
	}

	async function attach(files: Iterable<File>) {
		attachError = null;
		for (const file of files) {
			const result = await readImage(file);
			if (typeof result === 'string') attachError = result;
			else attachments.push(result);
		}
	}

	function onPaste(event: ClipboardEvent) {
		const files = [...(event.clipboardData?.files ?? [])].filter((f) => f.type.startsWith('image/'));
		if (files.length > 0) {
			event.preventDefault();
			void attach(files);
		}
	}

	// ——— 共用 ———

	function newExchange(requestBody: string): Exchange {
		return {
			requestBody,
			running: true,
			requestId: null,
			reply: emptyReply(),
			raw: '',
			record: null,
			loadingRecord: false,
			stopped: false
		};
	}

	async function deliver(
		exchange: Exchange,
		path: string,
		format: ApiFormat,
		abort: AbortController,
		kind: WorkbenchKind = 'chat'
	) {
		const result = await sendWorkbench(
			path,
			format,
			exchange.requestBody,
			abort.signal,
			(reply) => (exchange.reply = { ...reply }),
			kind
		);
		exchange.reply = { ...result.reply };
		exchange.raw = result.raw;
		exchange.requestId = result.requestId;
		exchange.stopped = result.stopped;
		exchange.running = false;
		if (result.requestId) {
			exchange.loadingRecord = true;
			exchange.record = await fetchRecord(result.requestId);
			exchange.loadingRecord = false;
		}
	}

	const toolFeeNotice = '开了搜索或生图时，上游另收的工具费没有算进这里的费用（Q217）。';
</script>

<svelte:head>
	<title>工作台 · 南天门</title>
</svelte:head>

<h1>工作台</h1>
<p class="muted">
	在这里手动向上游发测试请求。这些请求记在请求日志里（来源「工作台」），费用计入渠道的每日费用，不属于任何 API 密钥；失败不进冷却，也不影响健康度。
</p>

{#if loadError}
	<p class="alert" role="alert">{loadError}</p>
{/if}

<div class="tabs" role="tablist" aria-label="测试方式">
	{#each tabs as t (t.id)}
		<button
			type="button"
			role="tab"
			id={'tab-' + t.id}
			aria-selected={tab === t.id}
			aria-controls={'panel-' + t.id}
			class:primary={tab === t.id}
			onclick={() => (tab = t.id)}>{t.label}</button
		>
	{/each}
</div>

{#snippet kindInputs(kind: WorkbenchKind)}
	{#if kind === 'embedding'}
		<label class="field wide">
			<span>嵌入的输入文本</span>
			<input bind:value={settings.embeddingInput} />
		</label>
	{:else if kind === 'image'}
		<label class="field wide">
			<span>图片提示语</span>
			<input bind:value={settings.imagePrompt} />
		</label>
		<label class="field">
			<span>尺寸</span>
			<select bind:value={settings.imageSize}>
				{#each imageSizes as size (size)}<option value={size}>{size}</option>{/each}
				<option value="">不指定</option>
			</select>
		</label>
	{/if}
{/snippet}

<!-- 渠道测试 -->
<div class="panel" role="tabpanel" id="panel-channel" aria-labelledby="tab-channel" hidden={tab !== 'channel'}>
	<p class="muted">
		固定发到一个渠道，不走选路，用渠道自己的格式透传。可用时段、每日上限、RPM、并发和模型白名单都不拦，但用量照常计数；冷却中的密钥也照样发，成功一次就清掉这次证明可用的冷却。
	</p>
	<div class="form-grid">
		<label class="field">
			<span>渠道</span>
			<select value={channelId} onchange={(e) => pickChannel(e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}>
				<option value="">选一个渠道</option>
				{#each channels as c (c.id)}
					<option value={c.id}>{c.name}（{apiFormatLabels[c.apiFormat]}）{c.enabled ? '' : '（停用）'}</option>
				{/each}
			</select>
		</label>
		{#if detail}
			<label class="field">
				<span>渠道密钥</span>
				<select bind:value={keyId}>
					<option value="">自动（按优先级用启用的密钥）</option>
					{#each detail.keys as key (key.id)}
						<option value={key.id} disabled={!key.readable}>
							{key.name || key.prefix + '…'}{key.enabled ? '' : '（停用）'}{key.readable ? '' : '（解不开）'}
						</option>
					{/each}
				</select>
			</label>
		{/if}
		{#if channel?.apiFormat === 'openAiChat'}
			<label class="field">
				<span>测试类型</span>
				<select bind:value={settings.channelKind}>
					{#each workbenchKinds as k (k)}<option value={k}>{requestKindLabels[k]}</option>{/each}
				</select>
			</label>
		{/if}
		{#if channelKind === 'chat'}
			<label class="field wide">
				<span>测试提示语</span>
				<input bind:value={settings.channelPrompt} />
			</label>
		{:else}
			{@render kindInputs(channelKind)}
		{/if}
	</div>
	{#if channelKind === 'image'}<p class="notice">{imageCostNotice}</p>{/if}

	{#if detail}
		<fieldset class="models">
			<legend>上游模型（按顺序一个一个测）</legend>
			{#each detail.upstreamModels as m (m.name)}
				<label class="check"><input type="checkbox" checked={chosenModels.includes(m.name)} onchange={() => toggleModel(m.name)} />
					<span class="mono">{m.name}</span></label
				>
			{/each}
			{#each chosenModels.filter((name) => !detail?.upstreamModels.some((m) => m.name === name)) as name (name)}
				<label class="check"><input type="checkbox" checked onchange={() => toggleModel(name)} /> <span class="mono">{name}</span>
					<span class="muted">（不在模型列表里）</span></label
				>
			{/each}
		</fieldset>
		<form class="toolbar" onsubmit={addExtraModel}>
			<label class="field">
				<span>再加一个模型名</span>
				<input bind:value={extraModel} placeholder="还没同步进来的名字也行" />
			</label>
			<button type="submit" disabled={!extraModel.trim()}>加上</button>
		</form>

		{#if relevantCooldowns.length > 0}
			<ul class="notice">
				{#each relevantCooldowns as c, i (i)}
					<li>
						{cooldownScopeLabels[c.scope]}
						{#if c.keyPrefix}<span class="mono">{c.keyPrefix}…</span>{/if}
						{#if c.upstreamModel}<span class="mono">{c.upstreamModel}</span>{/if}
						冷却到 {formatUntil(c.until)}（{failureLabels[c.failure]}）。测试照样发，成功会清掉。
					</li>
				{/each}
			</ul>
		{/if}

		<div class="actions">
			<button class="primary" type="button" onclick={runChannelTest} disabled={channelRunning || chosenModels.length === 0}>
				{channelRunning ? '测试中…' : `测试 ${chosenModels.length} 个模型`}
			</button>
			{#if channelRunning}<button type="button" onclick={() => channelAbort?.abort()}>停止</button>{/if}
		</div>
	{/if}

	{#if rows.length > 0}
		<div class="scroll">
			<table>
				<thead>
					<tr>
						<th scope="col">上游模型</th>
						<th scope="col">结果</th>
						<th scope="col">密钥</th>
						<th scope="col" class="num">首字</th>
						<th scope="col" class="num">耗时</th>
						<th scope="col" class="num">输入</th>
						<th scope="col" class="num">输出</th>
						<th scope="col" class="num">费用</th>
						<th scope="col">回复</th>
					</tr>
				</thead>
				<tbody>
					{#each rows as row (row.model)}
						{@const last = row.record?.attempts.at(-1)}
						<tr>
							<td class="mono">{row.model}</td>
							<td>
								{#if row.state === 'waiting'}<span class="muted">排队</span>
								{:else if row.state === 'running'}<span class="muted">进行中</span>
								{:else if row.record?.status === 'succeeded'}<span class="ok">成功</span>
								{:else if row.reply.error || row.record}
									<span class="error">失败</span>
									{#if last && last.failureClass !== 'none'}<span class="muted">（{failureLabels[last.failureClass]}）</span>{/if}
								{:else}<span class="muted">—</span>{/if}
								{#if row.requestId}
									<a href={resolve('/requests/[id]', { id: row.requestId })}>详情</a>
								{/if}
							</td>
							<td class="mono">{last ? last.channelKeyPrefix + '…' : '—'}</td>
							<td class="num">{formatMs(row.record?.timeToFirstContentMs)}</td>
							<td class="num">{formatMs(row.record?.durationMs)}</td>
							<td class="num">{formatCount(row.record?.inputTokens)}</td>
							<td class="num">{formatCount(row.record?.outputTokens)}</td>
							<td class="num">{row.record?.unpriced ? '无单价' : formatCost(row.record?.costNanoUsd)}</td>
							<td class="answer">
								{#if row.reply.embedding}
									{row.reply.embedding.count} 条 × {row.reply.embedding.dimensions} 维
								{/if}
								{#each row.reply.images as src, i (src)}
									<img class="thumb" {src} alt={`${row.model} 生成的第 ${i + 1} 张图片`} />
								{/each}
								{#if row.reply.text}{row.reply.text}{/if}
								{#if row.reply.error || last?.errorMessage}<span class="error">{last?.errorMessage ?? row.reply.error}</span>{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</div>

<!-- 模型测试 -->
<div class="panel" role="tabpanel" id="panel-model" aria-labelledby="tab-model" hidden={tab !== 'model'}>
	<p class="muted">
		走正常的选路和失败切换，看最后落在哪个候选上、中间换过几次。照常遵守渠道的各项限制；冷却中的候选按不可用处理，失败也不写冷却。
	</p>
	<div class="form-grid">
		<label class="field">
			<span>下发模型</span>
			<select value={modelName} onchange={(e) => pickModel(e.currentTarget.value)}>
				<option value="">选一个下发模型</option>
				{#each models as m (m.id)}
					<option value={m.name}
						>{m.name}{m.requestKind === 'chat' ? '' : `（${requestKindLabels[m.requestKind]}）`}{m.enabled
							? ''
							: '（未启用）'}</option
					>
				{/each}
			</select>
		</label>
		{#if modelKind === 'chat'}
			<label class="field">
				<span>入站格式</span>
				<select bind:value={settings.modelFormat}>
					{#each formats as f (f)}<option value={f}>{apiFormatLabels[f]}</option>{/each}
				</select>
			</label>
			<label class="field wide">
				<span>测试提示语</span>
				<input bind:value={settings.modelPrompt} />
			</label>
		{:else}
			{@render kindInputs(modelKind)}
		{/if}
	</div>
	{#if modelKind === 'image'}<p class="notice">{imageCostNotice}</p>{/if}

	{#if modelDetail}
		{#if modelDetail.route.candidateCount === 0}
			<p class="alert">这个下发模型现在没有候选。</p>
		{:else}
			<ul class="candidates">
				{#each modelDetail.route.priorityGroups as group (group.priority)}
					<li>
						优先级 {group.priority}：
						{#each group.candidates as c, i (c.channelId + '/' + c.upstreamModel)}
							{@const f = channelFormats.get(c.channelId)}
							{i > 0 ? '、' : ''}{c.channelName} / <span class="mono">{c.upstreamModel}</span>
							{#if modelKind === 'chat' && f && f !== settings.modelFormat}<span class="badge" title={`渠道是 ${apiFormatLabels[f]}，请求会被转换`}>要转换</span>{/if}
						{/each}
					</li>
				{/each}
			</ul>
		{/if}
	{/if}

	<div class="actions">
		<button class="primary" type="button" onclick={runModelTest} disabled={!modelName || modelRun?.running}>
			{modelRun?.running ? '测试中…' : '发送'}
		</button>
		{#if modelRun?.running}<button type="button" onclick={() => modelAbort?.abort()}>停止</button>{/if}
	</div>

	{#if modelRun}
		<WorkbenchReply {...modelRun} />
	{/if}
</div>

<!-- 对话 -->
<div class="panel" role="tabpanel" id="panel-chat" aria-labelledby="tab-chat" hidden={tab !== 'chat'}>
	<div class="form-grid">
		<label class="field">
			<span>目标</span>
			<select bind:value={chatTarget} disabled={messages.length > 0}>
				<option value="model">下发模型（走选路）</option>
				<option value="channel">渠道 + 上游模型（不走选路）</option>
			</select>
		</label>
		{#if chatTarget === 'model'}
			<label class="field">
				<span>下发模型</span>
				<select bind:value={chatModel} disabled={messages.length > 0}>
					<option value="">选一个下发模型</option>
					{#each chatModels as m (m.id)}<option value={m.name}>{m.name}</option>{/each}
				</select>
			</label>
			<label class="field">
				<span>入站格式</span>
				<select bind:value={settings.modelFormat} disabled={messages.length > 0}>
					{#each formats as f (f)}<option value={f}>{apiFormatLabels[f]}</option>{/each}
				</select>
			</label>
		{:else}
			<label class="field">
				<span>渠道</span>
				<select
					value={chatChannelId}
					disabled={messages.length > 0}
					onchange={(e) => pickChatChannel(e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}
				>
					<option value="">选一个渠道</option>
					{#each channels as c (c.id)}
						<option value={c.id}>{c.name}（{apiFormatLabels[c.apiFormat]}）{c.enabled ? '' : '（停用）'}</option>
					{/each}
				</select>
			</label>
			<label class="field">
				<span>上游模型</span>
				<input bind:value={chatUpstreamModel} list="chat-upstream-models" disabled={messages.length > 0} />
				<datalist id="chat-upstream-models">
					{#each chatChannelModels as name (name)}<option value={name}></option>{/each}
				</datalist>
			</label>
		{/if}
	</div>

	<fieldset class="options">
		<legend>选项（{apiFormatLabels[chatFormat]}）</legend>
		<label class="field">
			<span>思考</span>
			<select bind:value={settings.effort}>
				{#each efforts as e (e)}<option value={e}>{effortLabels[e]}</option>{/each}
			</select>
		</label>
		<label class="check"><input type="checkbox" bind:checked={settings.search} /> 内置搜索</label>
		{#if imageGenerationAvailable}
			<label class="check"><input type="checkbox" bind:checked={settings.imageGeneration} /> 生图</label>
		{/if}
		<label class="check"><input type="checkbox" bind:checked={settings.stream} /> 流式</label>
	</fieldset>
	{#if settings.search && chatFormat === 'openAiChat'}
		<p class="muted small">Chat 格式的搜索是 web_search_options，只有专门的搜索模型认，别的模型会报错。</p>
	{/if}
	{#if settings.imageGeneration && imageGenerationAvailable && chatFormat === 'openAiChat'}
		<p class="muted small">Chat 格式的生图用 modalities:["image","text"]，这是 Gemini 和 OpenRouter 风格的写法，不是 OpenAI 官方的。</p>
	{/if}
	{#if settings.search || (settings.imageGeneration && imageGenerationAvailable)}
		<p class="notice small">{toolFeeNotice}</p>
	{/if}

	<ol class="messages">
		{#each messages as message, i (i)}
			<li class={message.turn.role}>
				{#if message.turn.role === 'user'}
					<p class="text">{message.turn.text}</p>
					{#each message.turn.images as image (image)}
						<img class="thumb" src={`data:${image.mediaType};base64,${image.data}`} alt={image.name} />
					{/each}
				{:else if message.exchange}
					<WorkbenchReply {...message.exchange} />
				{/if}
			</li>
		{/each}
	</ol>

	<form class="composer" onsubmit={sendChat}>
		{#if editRaw}
			<label class="field">
				<span>原始请求体（发出去的就是这份）</span>
				<textarea class="mono" rows="14" bind:value={rawDraft}></textarea>
			</label>
			{#if rawError}<p class="error">{rawError}</p>{/if}
		{:else}
			<label class="field">
				<span>消息（可以粘贴图片）</span>
				<textarea rows="3" bind:value={draft} onpaste={onPaste}
					onkeydown={(e) => {
						if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) e.currentTarget.form?.requestSubmit();
					}}
				></textarea>
			</label>
			{#if attachments.length > 0}
				<div class="attachments">
					{#each attachments as image, i (i)}
						<span class="attachment">
							<img class="thumb" src={`data:${image.mediaType};base64,${image.data}`} alt={image.name} />
							<button type="button" onclick={() => attachments.splice(i, 1)} aria-label={`去掉 ${image.name}`}>×</button>
						</span>
					{/each}
				</div>
			{/if}
			{#if attachError}<p class="error">{attachError}</p>{/if}
		{/if}
		<div class="actions">
			<button class="primary" type="submit" disabled={!chatReady || chatRunning}>发送</button>
			{#if chatRunning}<button type="button" onclick={() => chatAbort?.abort()}>停止</button>{/if}
			{#if !editRaw}
				<label class="file">
					<input
						type="file"
						accept="image/*"
						multiple
						onchange={(e) => {
							void attach(e.currentTarget.files ?? []);
							e.currentTarget.value = '';
						}}
					/>
					<span>添加图片</span>
				</label>
			{/if}
			<button type="button" onclick={openRawEditor} disabled={!chatReady || chatRunning}>
				{editRaw ? '不改原始 JSON' : '改原始 JSON'}
			</button>
			<button type="button" onclick={clearChat} disabled={messages.length === 0}>清空对话</button>
		</div>
		<p class="muted small">对话只在这个页面里，刷新就没了；设置会记在浏览器里。Ctrl/⌘ + Enter 发送。</p>
	</form>
</div>

<style>
	.tabs {
		display: flex;
		gap: 0.5rem;
		margin-bottom: 1rem;
	}
	[role='tabpanel'] > p:first-child {
		margin-top: 0;
	}
	.wide {
		grid-column: 1 / -1;
	}
	fieldset {
		border: 1px solid var(--border);
		border-radius: 4px;
		margin: 1rem 0;
		display: flex;
		flex-wrap: wrap;
		gap: 0.5rem 1.25rem;
		align-items: end;
	}
	.answer {
		max-width: 24rem;
		white-space: pre-wrap;
		overflow-wrap: anywhere;
	}
	.candidates {
		padding-left: 1.25rem;
	}
	.small {
		font-size: 0.85rem;
	}
	.messages {
		list-style: none;
		padding: 0;
		display: grid;
		gap: 0.75rem;
	}
	.messages li {
		border: 1px solid var(--border);
		border-radius: 6px;
		padding: 0.75rem;
	}
	.messages li.user {
		background: var(--bg);
	}
	.text {
		white-space: pre-wrap;
		overflow-wrap: anywhere;
		margin: 0;
	}
	.thumb {
		max-height: 6rem;
		max-width: 10rem;
		border: 1px solid var(--border);
		border-radius: 4px;
		margin: 0.25rem 0.25rem 0 0;
	}
	.attachments {
		display: flex;
		flex-wrap: wrap;
		gap: 0.5rem;
	}
	.attachment {
		position: relative;
	}
	.attachment button {
		position: absolute;
		top: 0;
		right: 0;
		padding: 0 0.4rem;
	}
	.file input {
		position: absolute;
		width: 1px;
		height: 1px;
		opacity: 0;
	}
	.file span {
		display: inline-block;
		border: 1px solid var(--border);
		border-radius: 4px;
		padding: 0.4rem 0.8rem;
		cursor: pointer;
	}
	.file:focus-within span {
		outline: 2px solid var(--focus);
	}
	.composer textarea {
		width: 100%;
	}
</style>
