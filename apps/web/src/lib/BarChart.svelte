<script lang="ts" module>
	export interface Series {
		label: string;
		color: string;
		values: number[];
	}
</script>

<script lang="ts">
	/**
	 * 手写的 SVG 柱状图（Q56）：一组时间点，每个点一根柱，柱可以按系列堆叠。
	 * 屏幕阅读器读不了图，所以旁边总有一份同样数据的表，用按钮切换显示（Q56）。
	 */
	interface Props {
		/** 图的标题，同时用作 SVG 的可访问名称。 */
		title: string;
		/** 每个时间点的标签，和 series 里每组 values 一一对应。 */
		labels: string[];
		/** 还没过完的时间点（当前小时 / 今天），画成虚边。 */
		inProgress: boolean[];
		series: Series[];
		format: (value: number) => string;
	}

	let { title, labels, inProgress, series, format }: Props = $props();

	const width = 720;
	const height = 200;
	const top = 12;
	const bottom = 24;
	const left = 4;
	const right = 4;

	let showTable = $state(false);

	const totals = $derived(labels.map((_, i) => series.reduce((sum, s) => sum + (s.values[i] ?? 0), 0)));
	const max = $derived(Math.max(0, ...totals));
	const slot = $derived(labels.length > 0 ? (width - left - right) / labels.length : 0);
	const bar = $derived(Math.max(1, slot * 0.7));

	/** 第 i 个点上第 k 个系列那一段的矩形。 */
	function segment(i: number, k: number): { x: number; y: number; h: number } {
		const plot = height - top - bottom;
		const below = series.slice(0, k).reduce((sum, s) => sum + (s.values[i] ?? 0), 0);
		const value = series[k].values[i] ?? 0;
		const scale = max > 0 ? plot / max : 0;
		return {
			x: left + i * slot + (slot - bar) / 2,
			y: top + plot - (below + value) * scale,
			h: value * scale
		};
	}

	/** 横轴标签太密时隔几个显示一个。 */
	const every = $derived(Math.max(1, Math.ceil(labels.length / 8)));

	function tooltip(i: number): string {
		const parts = series.map((s) => `${s.label} ${format(s.values[i] ?? 0)}`);
		return `${labels[i]}${inProgress[i] ? '（进行中）' : ''}：${parts.join('，')}`;
	}
</script>

<figure class="chart">
	<figcaption>
		<span>{title}</span>
		<span class="legend">
			{#each series as s (s.label)}
				<span><i style:background={s.color}></i>{s.label}</span>
			{/each}
		</span>
		<button type="button" class="link" aria-pressed={showTable} onclick={() => (showTable = !showTable)}>
			{showTable ? '隐藏数据表' : '显示数据表'}
		</button>
	</figcaption>

	<svg viewBox="0 0 {width} {height}" role="img" aria-label="{title}，最高 {format(max)}">
		<line x1={left} x2={width - right} y1={height - bottom} y2={height - bottom} class="axis" />
		{#each labels as label, i (i)}
			<g>
				<title>{tooltip(i)}</title>
				{#each series as s, k (s.label)}
					{@const r = segment(i, k)}
					{#if r.h > 0}
						<rect x={r.x} y={r.y} width={bar} height={r.h} fill={s.color} opacity={inProgress[i] ? 0.55 : 1} />
					{/if}
				{/each}
				{#if inProgress[i]}
					<rect
						x={left + i * slot + (slot - bar) / 2}
						y={top}
						width={bar}
						height={height - top - bottom}
						class="pending"
					/>
				{/if}
				{#if i % every === 0}
					<text x={left + i * slot + slot / 2} y={height - 6} text-anchor="middle">{label}</text>
				{/if}
			</g>
		{/each}
		{#if max > 0}
			<text x={left + 2} y={top + 10} class="max">{format(max)}</text>
		{/if}
	</svg>

	{#if showTable}
		<table>
			<caption class="visually-hidden">{title}</caption>
			<thead>
				<tr>
					<th scope="col">时间</th>
					{#each series as s (s.label)}
						<th scope="col" class="num">{s.label}</th>
					{/each}
				</tr>
			</thead>
			<tbody>
				{#each labels as label, i (i)}
					<tr>
						<th scope="row">{label}{inProgress[i] ? '（进行中）' : ''}</th>
						{#each series as s (s.label)}
							<td class="num">{format(s.values[i] ?? 0)}</td>
						{/each}
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</figure>

<style>
	.chart {
		margin: 0;
	}

	figcaption {
		display: flex;
		flex-wrap: wrap;
		gap: 0.5rem 1rem;
		align-items: baseline;
		margin-bottom: 0.5rem;
		font-weight: 600;
	}

	.legend {
		display: flex;
		gap: 0.75rem;
		font-weight: normal;
		color: var(--muted);
		font-size: 0.875rem;
	}

	.legend i {
		display: inline-block;
		width: 0.75rem;
		height: 0.75rem;
		margin-right: 0.25rem;
		border-radius: 2px;
		vertical-align: -1px;
	}

	button.link {
		margin-left: auto;
		font-weight: normal;
	}

	svg {
		display: block;
		width: 100%;
		height: auto;
	}

	svg text {
		font-size: 11px;
		fill: var(--muted);
	}

	.axis {
		stroke: var(--border);
	}

	.pending {
		fill: none;
		stroke: var(--muted);
		stroke-dasharray: 3 3;
	}

	table {
		margin-top: 0.5rem;
	}
</style>
