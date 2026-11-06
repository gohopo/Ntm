<script lang="ts">
	// 代码块。内容用字符串传进来，免得在 Svelte 标记里转义花括号。
	let { code, label = '' }: { code: string; label?: string } = $props();

	let copied = $state(false);

	async function copy() {
		await navigator.clipboard.writeText(code.trim());
		copied = true;
		setTimeout(() => (copied = false), 1500);
	}
</script>

<div class="code">
	<div class="code-bar">
		<span>{label}</span>
		<button type="button" onclick={copy}>{copied ? '已复制' : '复制'}</button>
	</div>
	<pre><code>{code.trim()}</code></pre>
</div>
