<script lang="ts">
	import { onMount } from 'svelte';
	import { api, ApiError, messageOf } from '#lib/api.ts';
	import type { TimeZoneView, VersionView } from '#lib/types.ts';
	import DebugSwitch from '#lib/DebugSwitch.svelte';

	let zone = $state<TimeZoneView | null>(null);
	let zoneError = $state<string | null>(null);
	let version = $state<VersionView | null>(null);

	onMount(() => {
		api.get<TimeZoneView>('/api/settings/time-zone').then(
			(value) => (zone = value),
			(e: unknown) => (zoneError = messageOf(e))
		);
		// 拿不到版本号不影响这一页，失败就不显示。
		api.get<VersionView>('/api/settings/version').then(
			(value) => (version = value),
			() => {}
		);
	});

	let currentPassword = $state('');
	let newPassword = $state('');
	let confirmPassword = $state('');
	let busy = $state(false);
	let fieldErrors = $state<Record<string, string | undefined>>({});
	let error = $state<string | null>(null);
	let done = $state(false);

	const mismatch = $derived(confirmPassword.length > 0 && confirmPassword !== newPassword);

	async function submit(event: SubmitEvent) {
		event.preventDefault();
		if (mismatch) return;
		busy = true;
		error = null;
		done = false;
		fieldErrors = {};
		try {
			await api.post('/api/auth/password', { currentPassword, newPassword });
			currentPassword = '';
			newPassword = '';
			confirmPassword = '';
			done = true;
		} catch (e) {
			if (e instanceof ApiError) {
				fieldErrors = {
					currentPassword: e.field('currentPassword'),
					newPassword: e.field('newPassword')
				};
				const rest = e.summary(['currentPassword', 'newPassword']);
				error = fieldErrors.currentPassword || fieldErrors.newPassword ? null : rest;
			} else {
				error = messageOf(e);
			}
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head>
	<title>设置 · 南天门</title>
</svelte:head>

<h1>设置</h1>

<section class="panel narrow" aria-labelledby="zone-heading">
	<h2 id="zone-heading">时区</h2>
	{#if zoneError}
		<p class="alert" role="alert">{zoneError}</p>
	{:else if zone}
		<p>
			网关时区 <strong class="mono">{zone.id}</strong>，现在是 {zone.localNow}。
		</p>
		{#if zone.isFallback}
			<p class="alert" role="alert">
				环境变量 TZ 设成了 <code>{zone.requested}</code>，但找不到这个时区（可能镜像里没有 tzdata），
				已回退到 UTC。
			</p>
		{/if}
		<p class="muted">
			渠道的每日费用上限和可用时段都按这个时区算。要改，设置容器的 <code>TZ</code> 环境变量后重启。
		</p>
	{:else}
		<p class="muted">加载中…</p>
	{/if}
</section>

<section class="panel wide" aria-labelledby="debug-heading">
	<h2 id="debug-heading">调试原文</h2>
	<DebugSwitch
		url="/api/settings/debug-capture"
		label="全局开关"
		hint="渠道和 API 密钥上也各有一个开关，任一开着就会录。"
	/>
</section>

<section class="panel narrow" aria-labelledby="password-heading">
	<h2 id="password-heading">修改管理员密码</h2>
	<p class="muted">改完之后，其他浏览器里的登录会全部失效；当前这个页面保持登录。</p>

	{#if done}
		<p class="notice" role="status">密码已修改。</p>
	{/if}
	{#if error}
		<p class="alert" role="alert">{error}</p>
	{/if}

	<form onsubmit={submit} novalidate>
		<label class="field">
			<span>当前密码</span>
			<input
				type="password"
				autocomplete="current-password"
				bind:value={currentPassword}
				aria-invalid={fieldErrors.currentPassword ? 'true' : undefined}
				aria-describedby={fieldErrors.currentPassword ? 'current-error' : undefined}
			/>
			{#if fieldErrors.currentPassword}
				<span id="current-error" class="error">{fieldErrors.currentPassword}</span>
			{/if}
		</label>
		<label class="field">
			<span>新密码</span>
			<input
				type="password"
				autocomplete="new-password"
				bind:value={newPassword}
				aria-invalid={fieldErrors.newPassword ? 'true' : undefined}
				aria-describedby={fieldErrors.newPassword ? 'new-error' : undefined}
			/>
			{#if fieldErrors.newPassword}
				<span id="new-error" class="error">{fieldErrors.newPassword}</span>
			{/if}
		</label>
		<label class="field">
			<span>再输一遍新密码</span>
			<input
				type="password"
				autocomplete="new-password"
				bind:value={confirmPassword}
				aria-invalid={mismatch ? 'true' : undefined}
				aria-describedby={mismatch ? 'confirm-error' : undefined}
			/>
			{#if mismatch}
				<span id="confirm-error" class="error">两次输入的新密码不一样。</span>
			{/if}
		</label>
		<div class="actions">
			<button
				class="primary"
				type="submit"
				disabled={busy || mismatch || !currentPassword || !newPassword || !confirmPassword}
			>
				{busy ? '保存中…' : '修改密码'}
			</button>
		</div>
	</form>
</section>

{#if version}
	<p class="muted version">
		南天门 <span class="mono">{version.version}</span>{#if version.commit}（提交 <span class="mono">{version.commit}</span>）{/if}
	</p>
{/if}

<style>
	.narrow {
		max-width: 28rem;
		margin-bottom: 1.5rem;
	}

	/* 调试原文那段说明比较长，28rem 挤成一团。 */
	.wide {
		max-width: 44rem;
		margin-bottom: 1.5rem;
	}

	.version {
		font-size: 0.85rem;
	}

	form {
		display: flex;
		flex-direction: column;
		gap: 0.75rem;
	}
</style>
