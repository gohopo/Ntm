<script lang="ts">
	import { onMount } from 'svelte';
	import { api, messageOf } from '#lib/api.ts';
	import type { DebugCaptureStatusView } from '#lib/types.ts';

	// 一处 DebugCapture 开关。全局接口多返回丢弃计数和上限，渠道/密钥只返回 until，
	// 缺的字段按默认值显示。
	let {
		url,
		label = '调试原文',
		hint = ''
	}: { url: string; label?: string; hint?: string } = $props();

	let state_ = $state<DebugCaptureStatusView | null>(null);
	let error = $state<string | null>(null);
	let busy = $state(false);
	let minutes = $state(60);
	let now = $state(Date.now());

	const until = $derived(state_?.until ? new Date(state_.until).getTime() : null);
	const remaining = $derived(until === null ? null : Math.max(0, Math.round((until - now) / 60000)));
	const on = $derived(remaining !== null && remaining > 0);
	const maxMinutes = $derived(state_?.maxDurationMinutes ?? 24 * 60);

	async function load() {
		try {
			state_ = await api.get<DebugCaptureStatusView>(url);
			if (state_.defaultDurationMinutes) minutes = state_.defaultDurationMinutes;
		} catch (e) {
			error = messageOf(e);
		}
	}

	async function set(value: number) {
		busy = true;
		error = null;
		try {
			state_ = await api.put<DebugCaptureStatusView>(url, { minutes: value });
			now = Date.now();
		} catch (e) {
			error = messageOf(e);
		} finally {
			busy = false;
		}
	}

	onMount(() => {
		void load();
		const timer = setInterval(() => (now = Date.now()), 30_000);
		return () => clearInterval(timer);
	});
</script>

<div class="debug">
	<div class="head">
		<strong>{label}</strong>
		{#if on}
			<span class="badge warn">开着，还剩 {remaining} 分钟</span>
		{:else}
			<span class="badge muted">关</span>
		{/if}
	</div>

	{#if error}
		<p class="alert" role="alert">{error}</p>
	{/if}

	{#if state_ && state_.dropped > 0}
		<p class="alert" role="alert">有 {state_.dropped} 份原文因为写入跟不上被丢掉了。</p>
	{/if}

	<div class="row">
		<label class="minutes">
			<span>开多久（分钟）</span>
			<input type="number" min="1" max={maxMinutes} bind:value={minutes} />
		</label>
		<button type="button" disabled={busy} onclick={() => set(minutes)}>
			{on ? '续时' : '打开'}
		</button>
		{#if on}
			<button type="button" class="danger" disabled={busy} onclick={() => set(0)}>关掉</button>
		{/if}
	</div>

	<!-- 中文换行会被折成空格，所以说明写在一行里。 -->
	<p class="note">打开后会把客户端原始请求、真正发给上游的请求和上游响应存下来，在请求详情里看。认证头会打码，正文原样保存，所以别开太久。到点自动关，最长 {Math.round(maxMinutes / 60)} 小时；原文保留 {state_?.retentionHours ?? 24} 小时。{hint}</p>
</div>

<style>
	.debug {
		display: flex;
		flex-direction: column;
		gap: 0.5rem;
	}

	.head {
		display: flex;
		align-items: center;
		gap: 0.5rem;
	}

	.row {
		display: flex;
		align-items: flex-end;
		gap: 0.5rem;
		flex-wrap: wrap;
	}

	.minutes {
		display: flex;
		flex-direction: column;
		gap: 0.25rem;
		font-size: 0.85rem;
	}

	.minutes input {
		width: 7rem;
	}

	.note {
		margin: 0;
		color: var(--muted);
		font-size: 0.85rem;
	}
</style>
