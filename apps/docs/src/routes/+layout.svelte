<script lang="ts">
	import '../app.css';
	import favicon from '#lib/assets/favicon.svg';
	import { onMount } from 'svelte';
	import { afterNavigate } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { groups, pages, repo } from '#lib/pages.ts';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();

	interface TocItem {
		id: string;
		text: string;
		level: number;
	}

	let article = $state<HTMLElement>();
	let toc = $state<TocItem[]>([]);
	let menuOpen = $state(false);

	// GitHub Pages 打开站点根时地址带结尾的 /，比较前去掉。
	const trim = (path: string) => (path.length > 1 ? path.replace(/\/+$/, '') : path);
	const index = $derived(pages.findIndex((p) => trim(resolve(p.path)) === trim(page.url.pathname)));
	const current = $derived(index >= 0 ? pages[index] : undefined);
	const previous = $derived(index > 0 ? pages[index - 1] : undefined);
	const next = $derived(index >= 0 && index < pages.length - 1 ? pages[index + 1] : undefined);

	// 页内目录从渲染出来的二、三级标题里取，换页后重新取。首次加载时 afterNavigate 早于 bind:this，所以挂载后也取一次；放到下一帧，等水合把标题文字填好（同一轮里读到的是空字符串）。
	function collectToc() {
		toc = article
			? [...article.querySelectorAll<HTMLHeadingElement>('h2[id], h3[id]')].map((heading) => ({
					id: heading.id,
					// 去掉标题末尾的「#」锚点链接。
					text: (heading.textContent ?? '').replace(/#\s*$/, '').trim(),
					level: heading.tagName === 'H2' ? 2 : 3
				}))
			: [];
	}

	onMount(() => {
		const frame = requestAnimationFrame(collectToc);
		return () => cancelAnimationFrame(frame);
	});
	afterNavigate(() => {
		menuOpen = false;
		collectToc();
	});
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
	<title>{current && current.path !== '/' ? `${current.title} · 南天门文档` : '南天门文档'}</title>
</svelte:head>

<header class="site-header">
	<button
		type="button"
		class="menu-button"
		aria-expanded={menuOpen}
		aria-controls="site-nav"
		onclick={() => (menuOpen = !menuOpen)}>目录</button
	>
	<a class="site-title" href={resolve('/')}>南天门文档</a>
	<a class="repo-link" href={repo}>GitHub</a>
</header>

<div class="site-body">
	<nav id="site-nav" class="site-nav" class:open={menuOpen} aria-label="文档目录">
		{#each groups as group (group.title)}
			<p class="nav-group">{group.title}</p>
			<ul>
				{#each group.pages as item (item.path)}
					<li>
						<a href={resolve(item.path)} aria-current={item === current ? 'page' : undefined}>{item.title}</a>
					</li>
				{/each}
			</ul>
		{/each}
	</nav>

	<main class="content">
		<article bind:this={article}>
			{@render children()}
		</article>

		{#if previous || next}
			<nav class="pager" aria-label="翻页">
				{#if previous}
					<a class="previous" href={resolve(previous.path)}><span>上一页</span>{previous.title}</a>
				{/if}
				{#if next}
					<a class="next" href={resolve(next.path)}><span>下一页</span>{next.title}</a>
				{/if}
			</nav>
		{/if}
	</main>

	<aside class="toc" aria-label="本页内容">
		{#if toc.length > 0}
			<p class="nav-group">本页内容</p>
			<ul>
				{#each toc as item, i (i)}
					<li class:sub={item.level === 3}><a href={`#${item.id}`}>{item.text}</a></li>
				{/each}
			</ul>
		{/if}
	</aside>
</div>
