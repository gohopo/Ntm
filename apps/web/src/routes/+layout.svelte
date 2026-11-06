<script lang="ts">
	import '../app.css';
	import favicon from '#lib/assets/favicon.svg';
	import { goto, invalidateAll } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api.ts';
	import type { LayoutProps } from './$types';

	let { children, data }: LayoutProps = $props();

	const links = [
		{ href: resolve('/'), label: '总览', match: ['/'] },
		{ href: resolve('/requests'), label: '请求日志', match: ['/requests'] },
		{ href: resolve('/channels'), label: '渠道', match: ['/channels'] },
		{ href: resolve('/models'), label: '下发模型', match: ['/models'] },
		{ href: resolve('/workbench'), label: '工作台', match: ['/workbench'] },
		{ href: resolve('/prices'), label: '单价', match: ['/prices'] },
		{ href: resolve('/api-keys'), label: 'API 密钥', match: ['/api-keys'] },
		{ href: resolve('/settings'), label: '设置', match: ['/settings'] }
	];

	function isCurrent(match: string[]): boolean {
		const path = page.url.pathname;
		return match.some((m) => (m === '/' ? path === resolve('/') : path.startsWith(m)));
	}

	async function signOut() {
		try {
			await api.post('/api/auth/logout');
		} finally {
			await invalidateAll();
			await goto(resolve('/login'));
		}
	}
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
	<title>南天门</title>
</svelte:head>

{#if data.signedIn}
	<header>
		<a class="brand" href={resolve('/')}>南天门</a>
		<nav aria-label="主导航">
			<ul>
				{#each links as link (link.href)}
					<li>
						<a href={link.href} aria-current={isCurrent(link.match) ? 'page' : undefined}>
							{link.label}
						</a>
					</li>
				{/each}
			</ul>
		</nav>
		<button type="button" onclick={signOut}>退出登录</button>
	</header>
{/if}

<main>
	{@render children()}
</main>

<style>
	header {
		display: flex;
		align-items: center;
		gap: 1.5rem;
		padding: 0.5rem 1.5rem;
		background: var(--panel);
		border-bottom: 1px solid var(--border);
	}

	.brand {
		font-weight: 700;
		color: inherit;
		text-decoration: none;
	}

	nav {
		flex: 1;
	}

	nav ul {
		display: flex;
		flex-wrap: wrap;
		gap: 0.25rem;
		list-style: none;
		margin: 0;
		padding: 0;
	}

	nav a {
		display: block;
		padding: 0.3rem 0.7rem;
		border-radius: 4px;
		text-decoration: none;
		color: var(--text);
	}

	nav a[aria-current='page'] {
		background: var(--bg);
		font-weight: 600;
		color: var(--accent);
	}

	main {
		max-width: 80rem;
		margin: 0 auto;
		padding: 1.5rem;
	}
</style>
