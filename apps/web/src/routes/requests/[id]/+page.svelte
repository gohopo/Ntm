<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import DebugPayloads from '#lib/DebugPayloads.svelte';
	import {
		affinityLabels,
		blockReasonLabels,
		apiFormatLabels,
		cooldownScopeLabels,
		failureLabels,
		formatCost,
		formatCount,
		formatMs,
		formatMultiplier,
		formatPrice,
		formatTime,
		requestKindLabels,
		statusLabels
	} from '#lib/format.ts';
	import { LiveFeed } from '#lib/live.svelte.ts';
	import type { AttemptView, LiveEvent, RequestDetailView } from '#lib/types.ts';

	/** 费用拆解的五行：用了 Token 的那几项才列出来。 */
	function parts(attempt: AttemptView) {
		return [
			{ label: '非缓存输入', tokens: attempt.inputTokens, price: attempt.inputPrice },
			{ label: '缓存读', tokens: attempt.cacheReadTokens, price: attempt.cacheReadPrice },
			{ label: '缓存写 5m', tokens: attempt.cacheWrite5mTokens, price: attempt.cacheWrite5mPrice },
			{ label: '缓存写 1h', tokens: attempt.cacheWrite1hTokens, price: attempt.cacheWrite1hPrice },
			{
				label: attempt.sawThinking ? '输出（思考模式）' : '输出',
				tokens: attempt.outputTokens,
				price: attempt.outputPrice
			}
		].filter((part) => part.tokens > 0);
	}

	/** 这一项的小计（纳美元）：Token × 单价 × 倍率。缺单价就是 null。 */
	function subtotal(tokens: number, price: number | null, basisPoints: number): number | null {
		return price === null ? null : Math.round(tokens * price * 1000 * (basisPoints / 10_000));
	}

	/** 按张计费的尝试（Q230）：有每张图片单价快照、返回了图片。这时不看 Token。 */
	function perImage(attempt: AttemptView): boolean {
		return attempt.imagePrice !== null && attempt.imageCount > 0;
	}

	/** 有单价快照、并且确实消耗了 Token 或按张计费的尝试才需要拆解。 */
	function billed(attempt: AttemptView): boolean {
		return perImage(attempt) || parts(attempt).length > 0;
	}

	let detail = $state<RequestDetailView | null>(null);
	let error = $state<string | null>(null);
	let interruptError = $state<string | null>(null);
	const running = $derived(detail?.status === 'running');

	function path(id: string): string {
		return '/api/requests/' + encodeURIComponent(id);
	}

	$effect(() => {
		const id = page.params.id ?? '';
		detail = null;
		error = null;
		interruptError = null;
		api.get<RequestDetailView>(path(id)).then(
			(value) => (detail = value),
			(e: unknown) => (error = messageOf(e))
		);
	});

	// 进行中的请求跟着实时链路刷新（Q112）。结束后记录是异步分批写盘的，
	// 刚结束的一小会儿里两边都查不到（404），所以隔一会儿重试几次，期间保留旧的内容。
	$effect(() => {
		if (!running) return;
		const id = detail!.id;
		let pending: ReturnType<typeof setTimeout> | undefined;
		let cancelled = false;

		async function refresh(retries: number) {
			try {
				const value = await api.get<RequestDetailView>(path(id));
				if (!cancelled) detail = value;
			} catch (e) {
				if (cancelled) return;
				if (e instanceof ApiError && e.status === 404 && retries > 0) {
					pending = setTimeout(() => void refresh(retries - 1), 500);
				} else {
					error = messageOf(e);
				}
			}
		}

		function schedule(retries: number) {
			clearTimeout(pending);
			pending = setTimeout(() => void refresh(retries), 200);
		}

		const feed = new LiveFeed((event: LiveEvent) => {
			if (event.type === 'snapshot') {
				schedule(10);
			} else if ('requestId' in event && event.requestId === id) {
				schedule(event.type === 'requestFinished' ? 10 : 0);
			}
		});
		return () => {
			cancelled = true;
			clearTimeout(pending);
			feed.close();
		};
	});

	async function interrupt() {
		if (!detail) return;
		if (!confirm('中断这个请求？客户端会收到错误，不会换渠道重试。')) return;
		interruptError = null;
		try {
			await api.post(path(detail.id) + '/interrupt');
			detail = { ...detail, interruptRequested: true };
		} catch (e) {
			interruptError = e instanceof ApiError && e.status === 404 ? '这个请求已经结束了。' : messageOf(e);
		}
	}
</script>

<svelte:head>
	<title>请求详情 · 南天门</title>
</svelte:head>

<p><a href={resolve('/requests')}>← 返回请求日志</a></p>
<h1>请求详情</h1>

{#if interruptError}
	<p class="alert" role="alert">{interruptError}</p>
{/if}

{#if error}
	<p class="alert" role="alert">{error}</p>
{:else if !detail}
	<p class="muted">加载中…</p>
{:else}
	<dl class="panel facts">
		<div><dt>请求 ID</dt><dd class="mono">{detail.id}</dd></div>
		<div>
			<dt>下发模型</dt>
			<dd>
				<span class="mono">{detail.publishedModel}</span>
				<span class="badge">{requestKindLabels[detail.requestKind]}</span>
			</dd>
		</div>
		<div>
			<dt>状态</dt>
			<dd>
				{statusLabels[detail.status]}
				{#if detail.httpStatusCode}<span class="muted">HTTP {detail.httpStatusCode}</span>{/if}
				{#if detail.clientDisconnected}<span class="badge warn">客户端已断开</span>{/if}
				{#if running}
					{#if detail.interruptRequested}
						<span class="badge warn">正在中断</span>
					{:else}
						<button type="button" class="danger" onclick={interrupt}>中断</button>
					{/if}
				{/if}
			</dd>
		</div>
		<div>
			<dt>亲和</dt>
			<dd>
				{#if detail.affinity}
					{affinityLabels[detail.affinity]}
				{:else}
					<span class="muted"
						>{detail.requestKind !== 'chat'
							? '不适用（只有对话有亲和）'
							: running || detail.attempts.length === 0
								? '—'
								: '认不出所属的 Conversation'}</span
					>
				{/if}
			</dd>
		</div>
		<div>
			<dt>API 密钥</dt>
			<dd>
				{#if detail.origin === 'workbench'}<span class="badge">工作台</span>
					<span class="muted">Admin 在工作台发的，不属于任何 API 密钥</span>{:else}{detail.apiKeyName ?? '—'}{/if}
			</dd>
		</div>
		<div>
			<dt>入站格式</dt>
			<dd>{apiFormatLabels[detail.inboundApiFormat]}{detail.isStream ? '，流式' : ''}</dd>
		</div>
		<div><dt>开始</dt><dd>{formatTime(detail.startedAt)}</dd></div>
		<div><dt>结束</dt><dd>{formatTime(detail.finishedAt)}</dd></div>
		<div><dt>首字</dt><dd>{formatMs(detail.timeToFirstContentMs)}</dd></div>
		<div><dt>耗时</dt><dd>{formatMs(detail.durationMs)}</dd></div>
		<div>
			<dt>用量</dt>
			<dd>
				{#if running}
					<span class="muted">结束后才有</span>
				{:else}
					输入 {formatCount(detail.inputTokens)}，缓存读 {formatCount(detail.cacheReadTokens)}，
					缓存写 {formatCount(detail.cacheWrite5mTokens + detail.cacheWrite1hTokens)}，
					输出 {formatCount(detail.outputTokens)}
					{#if detail.usageSource === 'gatewayEstimated'}<span class="muted">（网关估算）</span>{/if}
					{#if detail.usageSource === 'unavailable'}<span class="muted">（上游没给用量）</span>{/if}
					{#if detail.requestKind === 'image'}<br />返回图片 {formatCount(detail.imageCount)} 张{/if}
				{/if}
			</dd>
		</div>
		<div>
			<dt>费用</dt>
			<dd>
				{#if detail.unpriced}
					<span class="badge">无单价</span>
					<span class="muted">
						{#if detail.imageCount > 0}
							生成了图片，但这个上游模型没有每张图片的单价，也没有能算的 Token 用量。
						{:else}
							用了 Token，但这个上游模型没有单价。
						{/if}
					</span>
				{:else}
					{formatCost(detail.costNanoUsd)}
					{#if detail.usageSource === 'gatewayEstimated' && detail.costNanoUsd !== null}
						<span class="badge">估</span>
					{/if}
				{/if}
			</dd>
		</div>
	</dl>

	{#if detail.skipped && detail.skipped.length > 0}
		<h2>跳过的候选（{detail.skipped.length}）</h2>
		<p class="muted">选路时这些候选被冷却或限制挡掉了，没有发请求给它们。</p>
		<table>
			<thead>
				<tr>
					<th scope="col">渠道</th>
					<th scope="col">上游模型</th>
					<th scope="col">原因</th>
					<th scope="col">预计恢复</th>
				</tr>
			</thead>
			<tbody>
				{#each detail.skipped as item (item.channelId + ':' + item.upstreamModel)}
					<tr>
						<td><a href={resolve('/channels/[id]', { id: String(item.channelId) })}>{item.channelName}</a></td>
						<td class="mono">{item.upstreamModel}</td>
						<td>{blockReasonLabels[item.reason]}</td>
						<td>{item.recoversAt ? formatTime(item.recoversAt) : '说不准'}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}

	<h2>尝试（{detail.attempts.length}）</h2>
	{#if detail.attempts.length === 0}
		{#if detail.blockedBy || detail.blockDetail}
			<p class="alert">
				没有发出任何上游请求{detail.blockedBy ? `：${failureLabels[detail.blockedBy]}` : ''}。
				{#if detail.blockDetail}<br />{detail.blockDetail}{/if}
			</p>
		{:else}
			<p class="muted">没有发出任何上游请求（没有可用的候选）。</p>
		{/if}
	{:else}
		<div class="scroll">
			<table>
				<thead>
					<tr>
						<th scope="col" class="num">#</th>
						<th scope="col">渠道 / 渠道密钥</th>
						<th scope="col">上游模型</th>
						<th scope="col">结果</th>
						<th scope="col">冷却</th>
						<th scope="col" class="num">首事件 / 首字</th>
						<th scope="col" class="num">耗时</th>
						<th scope="col" class="num">输入 / 输出</th>
						<th scope="col" class="num">费用</th>
					</tr>
				</thead>
				<tbody>
					{#each detail.attempts as attempt (attempt.ordinal)}
						<tr>
							<td class="num">{attempt.ordinal}</td>
							<td>
								<a href={resolve('/channels/[id]', { id: String(attempt.channelId) })}>
									{attempt.channelName}
								</a>
								<br /><span class="mono muted">{attempt.channelKeyPrefix}…</span>
							</td>
							<td>
								<span class="mono">{attempt.upstreamModel}</span>
								<br /><span class="muted">
									{apiFormatLabels[attempt.upstreamApiFormat]}{attempt.passthrough ? '，直通' : '，转换'}
								</span>
							</td>
							<td>
								{#if attempt.inProgress}
									<span class="muted">{attempt.committed ? '已提交，输出中' : '进行中'}</span>
								{:else if attempt.committed && attempt.failureClass !== 'none'}
									<span class="ok">已提交</span><span class="muted">，之后</span>
									<span class="error">{failureLabels[attempt.failureClass]}</span>
								{:else if attempt.committed}
									<span class="ok">已提交</span>
								{:else if attempt.failureClass === 'none'}
									<span class="muted">—</span>
								{:else}
									<span class="error">{failureLabels[attempt.failureClass]}</span>
								{/if}
								{#if attempt.httpStatusCode}<span class="muted"> HTTP {attempt.httpStatusCode}</span>{/if}
								{#if attempt.errorMessage}<br /><span class="muted">{attempt.errorMessage}</span>{/if}
								{#if attempt.responseId}
									<br /><span class="muted">响应 id</span> <span class="mono">{attempt.responseId}</span>
								{/if}
							</td>
							<td>
								{#if attempt.cooldownScope !== 'none'}
									{cooldownScopeLabels[attempt.cooldownScope]}
									{#if attempt.cooldownUntil}<br /><span class="muted">到 {formatTime(attempt.cooldownUntil)}</span>{/if}
								{:else}
									<span class="muted">—</span>
								{/if}
							</td>
							<td class="num">{formatMs(attempt.firstEventMs)} / {formatMs(attempt.firstContentMs)}</td>
							<td class="num">{formatMs(attempt.durationMs)}</td>
							<td class="num">{formatCount(attempt.inputTokens)} / {formatCount(attempt.outputTokens)}</td>
							<td class="num">
								{#if attempt.unpriced}
									<span class="badge">无单价</span>
								{:else}
									{formatCost(attempt.costNanoUsd)}
								{/if}
							</td>
						</tr>
						<tr class="uri">
							<td></td>
							<td colspan="8" class="mono muted">{attempt.requestUri}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>

		{#each detail.attempts.filter(billed) as attempt (attempt.ordinal)}
			<h3>第 {attempt.ordinal} 次尝试的费用</h3>
			<p class="muted">
				单价是当时用的那一份快照，之后改单价不会改这条记录。倍率 {formatMultiplier(
					attempt.multiplierBasisPoints
				)}{#if perImage(attempt)}，按返回的图片张数计费，不看 Token{:else if attempt.longContextOver !== null}，按「输入超过 {formatCount(
						attempt.longContextOver
					)}」那组长上下文单价{/if}。
			</p>
			{#if perImage(attempt)}
				<div class="scroll">
					<table>
						<thead>
							<tr>
								<th scope="col">项</th>
								<th scope="col" class="num">张数</th>
								<th scope="col" class="num">单价 / 张</th>
								<th scope="col" class="num">小计</th>
							</tr>
						</thead>
						<tbody>
							<tr>
								<th scope="row">图片</th>
								<td class="num">{formatCount(attempt.imageCount)}</td>
								<td class="num">{formatPrice(attempt.imagePrice)}</td>
								<td class="num">{formatCost(attempt.costNanoUsd)}</td>
							</tr>
						</tbody>
					</table>
				</div>
			{:else}
			<div class="scroll">
				<table>
					<thead>
						<tr>
							<th scope="col">项</th>
							<th scope="col" class="num">Token</th>
							<th scope="col" class="num">单价 / 百万</th>
							<th scope="col" class="num">小计</th>
						</tr>
					</thead>
					<tbody>
						{#each parts(attempt) as part (part.label)}
							<tr>
								<th scope="row">{part.label}</th>
								<td class="num">{formatCount(part.tokens)}</td>
								<td class="num">{formatPrice(part.price)}</td>
								<td class="num">
									{#if part.price === null}
										<span class="badge">无单价</span>
									{:else}
										{formatCost(subtotal(part.tokens, part.price, attempt.multiplierBasisPoints))}
									{/if}
								</td>
							</tr>
						{/each}
						<tr>
							<th scope="row">合计</th>
							<td class="num"></td>
							<td class="num"></td>
							<td class="num">
								{#if attempt.unpriced}
									<span class="badge">无单价</span>
								{:else}
									{formatCost(attempt.costNanoUsd)}
								{/if}
							</td>
						</tr>
					</tbody>
				</table>
			</div>
			{/if}
		{/each}
	{/if}

	<DebugPayloads requestId={detail.id} />
{/if}

<style>
	.facts {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
		gap: 0.5rem 1.5rem;
		margin: 0;
	}

	.facts dt {
		font-size: 0.85rem;
		color: var(--muted);
	}

	.facts dd {
		margin: 0;
		overflow-wrap: anywhere;
	}

	.scroll {
		overflow-x: auto;
	}

	tr.uri td {
		padding-top: 0;
		font-size: 0.8rem;
		overflow-wrap: anywhere;
	}
</style>
