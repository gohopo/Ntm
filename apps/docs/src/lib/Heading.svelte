<script lang="ts">
	// 带固定 id 的标题：页内目录和站内的 #锚点链接都靠它（Q325）。
	import type { Snippet } from 'svelte';

	let { id, level = 2, children }: { id: string; level?: 2 | 3; children: Snippet } = $props();
</script>

{#if level === 2}
	<h2 {id}>{@render children()}<a class="anchor" href={`#${id}`} aria-label="链接到这一节">#</a></h2>
{:else}
	<h3 {id}>{@render children()}<a class="anchor" href={`#${id}`} aria-label="链接到这一节">#</a></h3>
{/if}
