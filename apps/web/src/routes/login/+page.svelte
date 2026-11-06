<script lang="ts">
	import { goto, invalidateAll } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api, ApiError, messageOf } from '#lib/api.ts';

	let password = $state('');
	let busy = $state(false);
	let error = $state<string | null>(null);

	/** 只接受站内路径，挡住 ?next=//evil.example 这类跳转。 */
	function nextPath(): string {
		const next = page.url.searchParams.get('next');
		return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\')
			? next
			: resolve('/');
	}

	async function submit(event: SubmitEvent) {
		event.preventDefault();
		busy = true;
		error = null;
		try {
			await api.post('/api/auth/login', { password });
			await invalidateAll();
			await goto(nextPath(), { replaceState: true });
		} catch (e) {
			error = e instanceof ApiError ? (e.field('password') ?? e.message) : messageOf(e);
			password = '';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head>
	<title>登录 · 南天门</title>
</svelte:head>

<div class="login panel">
	<h1>南天门</h1>
	<form onsubmit={submit} novalidate>
		<label class="field">
			<span>管理员密码</span>
			<!-- svelte-ignore a11y_autofocus -->
			<input
				type="password"
				name="password"
				autocomplete="current-password"
				required
				autofocus
				bind:value={password}
				aria-invalid={error ? 'true' : undefined}
				aria-describedby={error ? 'login-error' : undefined}
			/>
		</label>
		{#if error}
			<p id="login-error" class="error" role="alert">{error}</p>
		{/if}
		<div class="actions">
			<button class="primary" type="submit" disabled={busy || password.length === 0}>
				{busy ? '登录中…' : '登录'}
			</button>
		</div>
	</form>
	<p class="muted hint">忘了密码就在服务器上运行 <code>reset-admin-password</code> 命令。</p>
</div>

<style>
	.login {
		max-width: 22rem;
		margin: 15vh auto 0;
	}

	.hint {
		font-size: 0.85rem;
		margin: 1rem 0 0;
	}
</style>
