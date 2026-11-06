<script lang="ts">
	import type { ApiError } from '#lib/api.ts';
	import type { TimeWindowView } from '#lib/types.ts';

	let {
		windows = $bindable(),
		error = null
	}: { windows: TimeWindowView[]; error?: ApiError | null } = $props();

	const weekdayNames = ['日', '一', '二', '三', '四', '五', '六'];

	function add() {
		windows = [...windows, { weekdays: [1, 2, 3, 4, 5], start: '09:00', end: '18:00' }];
	}

	function removeAt(index: number) {
		windows = windows.filter((_, i) => i !== index);
	}

	function toggleDay(index: number, day: number, on: boolean) {
		const next = on
			? [...windows[index].weekdays, day].sort((a, b) => a - b)
			: windows[index].weekdays.filter((d) => d !== day);
		windows = windows.map((w, i) => (i === index ? { ...w, weekdays: next } : w));
	}
</script>

{#each windows as window, index (index)}
	<div class="window">
		<div class="days">
			{#each weekdayNames as name, day (day)}
				<label class="day">
					<input
						type="checkbox"
						checked={window.weekdays.includes(day)}
						onchange={(event) => toggleDay(index, day, event.currentTarget.checked)}
					/>
					<span>{name}</span>
				</label>
			{/each}
		</div>
		<label class="field time">
			<span>开始</span>
			<input type="time" bind:value={windows[index].start} required />
		</label>
		<label class="field time">
			<span>结束</span>
			<input type="time" bind:value={windows[index].end} required />
		</label>
		<button type="button" class="danger" onclick={() => removeAt(index)}>删掉这段</button>
		{#if window.start > window.end}
			<span class="hint">跨午夜：算在开始那天。</span>
		{:else if window.start === window.end}
			<span class="hint">开始和结束一样：选中的那几天整天可用。</span>
		{/if}
		{#each ['weekdays', 'start', 'end'] as part (part)}
			{#if error?.field(`timeWindows[${index}].${part}`)}
				<span class="error">{error.field(`timeWindows[${index}].${part}`)}</span>
			{/if}
		{/each}
	</div>
{:else}
	<p class="muted">没有时段限制，随时可用。</p>
{/each}

{#if error?.field('timeWindows')}
	<p class="error">{error.field('timeWindows')}</p>
{/if}

<button type="button" onclick={add}>加一段可用时段</button>

<style>
	.window {
		display: flex;
		flex-wrap: wrap;
		gap: 0.6rem;
		align-items: flex-end;
		padding: 0.6rem 0;
		border-top: 1px solid var(--line);
	}

	.days {
		display: flex;
		gap: 0.3rem;
	}

	.day {
		display: flex;
		flex-direction: column;
		align-items: center;
		font-size: 0.85rem;
	}

	.hint,
	.error {
		font-size: 0.85rem;
	}

	.hint {
		color: var(--muted);
	}

	.time input {
		width: 7.5rem;
	}
</style>
