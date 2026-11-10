<script lang="ts">
	import { resolve } from '$app/paths';
	import { Code, Heading } from '#lib';

	type Row = { key: string; value: string; note: string };

	const data: Row[] = [
		{ key: 'Ntm__Data__Directory', value: 'data', note: '数据目录。相对路径从工作目录算起，镜像里是 /app/data。必须在本地盘上。' },
		{ key: 'Ntm__Data__ConfigDatabaseFileName', value: 'ntm.db', note: '配置库的文件名。' },
		{ key: 'Ntm__Data__TrafficDatabaseFileName', value: 'ntm-traffic.db', note: '请求记录库的文件名。' },
		{ key: 'Ntm__Data__DebugDatabaseFileName', value: 'ntm-debug.db', note: '调试原文库的文件名。' }
	];

	const traffic: Row[] = [
		{ key: 'Ntm__Traffic__RetentionDays', value: '7', note: '请求记录保留几天。0 或负数表示永久保留。每小时汇总不受影响，一直保留。' },
		{ key: 'Ntm__Traffic__DeleteBatchSize', value: '500', note: '清理过期记录时每批删多少条。' },
		{ key: 'Ntm__Traffic__LandingRetentionDays', value: '30', note: 'Responses 落点（previous_response_id、conversation 指向哪个渠道密钥）在最后一次使用后保留几天。0 或负数表示永久保留。' },
		{ key: 'Ntm__Traffic__AuditRetentionDays', value: '180', note: '操作记录保留几天。0 或负数表示永久保留；不受请求记录保留期影响。' },
		{ key: 'Ntm__Traffic__MaxLandings', value: '1000000', note: '落点最多留多少条，超出时先删最久没用的。' }
	];

	const debug: Row[] = [
		{ key: 'Ntm__Debug__RetentionHours', value: '24', note: '调试原文保留几小时。' },
		{ key: 'Ntm__Debug__DefaultDurationMinutes', value: '60', note: '打开调试原文时默认开多久。' },
		{ key: 'Ntm__Debug__MaxDurationHours', value: '24', note: '一次最多能开多久。' },
		{ key: 'Ntm__Debug__QueueCapacity', value: '256', note: '等待写盘的原文最多排多少份。满了就丢掉新的并计数，不会拖慢转发。' },
		{ key: 'Ntm__Debug__WriteBatchSize', value: '32', note: '一次写盘最多攒多少份。' },
		{ key: 'Ntm__Debug__CleanupInterval', value: '00:15:00', note: '多久清理一次过期原文，格式是 时:分:秒。' }
	];

	const pricing: Row[] = [
		{ key: 'Ntm__Pricing__CatalogUrl', value: 'https://models.dev/api.json', note: '价目目录的地址。' },
		{ key: 'Ntm__Pricing__RefreshIntervalHours', value: '24', note: '多久刷新一次价目目录。0 表示不自动刷新，启动时也不下载，只能在「单价」页点「立即刷新」。' },
		{ key: 'Ntm__Pricing__CacheFileName', value: 'price-catalog.json', note: '价目目录在数据目录里的缓存文件名。' },
		{ key: 'Ntm__Pricing__RequestTimeoutSeconds', value: '120', note: '下载价目目录的超时秒数。' },
		{ key: 'Ntm__Pricing__MaxCatalogBytes', value: '33554432', note: '价目目录解压后的大小上限（字节），默认 32 MiB。超过按下载失败处理，保留旧目录。' }
	];

	const modelSync: Row[] = [
		{ key: 'Ntm__ModelSync__IntervalMinutes', value: '60', note: '多久自动同步一次勾了「自动同步上游模型清单」的渠道。0 表示关掉自动同步，只能手动点「立即同步」。' },
		{ key: 'Ntm__ModelSync__StartDelaySeconds', value: '60', note: '网关启动后等多久做第一次自动同步。' }
	];

	const seed: Row[] = [
		{ key: 'Ntm__Seed__ChannelKey', value: '（无）', note: '种子渠道的渠道密钥。不填就不建种子渠道。' },
		{ key: 'Ntm__Seed__BaseUrl', value: '（无）', note: '种子渠道的上游地址，必须是 http 或 https 地址，否则网关拒绝启动。' },
		{ key: 'Ntm__Seed__Path', value: '（无）', note: '种子渠道的请求路径，不填按 API 格式的默认路径。' },
		{ key: 'Ntm__Seed__ApiFormat', value: 'OpenAiChat', note: '种子渠道的 API 格式：OpenAiChat、OpenAiResponses 或 Anthropic。' },
		{ key: 'Ntm__Seed__ChannelName', value: 'default', note: '种子渠道的名字。' }
	];

	const metrics: Row[] = [
		{ key: 'Ntm__Metrics__Enabled', value: 'false', note: '是否开放 /metrics（Prometheus 文本格式）。不开时这个地址不存在。' },
		{ key: 'Ntm__Metrics__Token', value: '（无）', note: '抓取时要带的令牌：Authorization: Bearer 令牌。不填则谁都能抓，启动时会警告；正式部署应设置令牌并限制网络访问。' }
	];

	const sections: { id: string; title: string; intro: string; rows: Row[] }[] = [
		{ id: 'data', title: '数据（Ntm:Data）', intro: '数据放在哪。', rows: data },
		{ id: 'traffic', title: '记录与落点（Ntm:Traffic）', intro: '请求记录、操作记录和 Responses 落点保留多久。', rows: traffic },
		{ id: 'debug', title: '调试原文（Ntm:Debug）', intro: '调试原文怎么存、存多久。', rows: debug },
		{ id: 'pricing', title: '价目目录（Ntm:Pricing）', intro: '自动单价从哪里来。', rows: pricing },
		{ id: 'model-sync', title: '模型同步（Ntm:ModelSync）', intro: '自动同步上游模型清单的节奏。', rows: modelSync },
		{ id: 'metrics', title: '指标（Ntm:Metrics）', intro: '给 Prometheus 抓的指标，默认关。指标有哪些见「部署与运维」的指标一节。', rows: metrics },
		{
			id: 'seed',
			title: '种子渠道（Ntm:Seed）',
			intro: '只在库里还没有任何渠道、而且填了 Ntm__Seed__ChannelKey 时用一次：第一次启动就建好一个渠道。之后改这些不再有作用。',
			rows: seed
		}
	];

	const compose = `services:
  ntm:
    environment:
      TZ: Asia/Shanghai
      Ntm__Traffic__RetentionDays: "30"
      Ntm__ModelSync__IntervalMinutes: "0"`;
</script>

<h1>配置参考</h1>

<p class="lead">
	网关的配置都有默认值，大多数部署只需要改 <a href={resolve('/deploy') + '#docker'}>compose.yaml 里的三个环境变量</a>。这一页列出全部可改的项。
</p>

<Heading id="how">怎么改</Heading>

<p>
	每一项都可以用环境变量覆盖，名字是「Ntm__节__项」，中间是两个下划线。例如在 <code>compose.yaml</code> 里：
</p>

<Code code={compose} label="compose.yaml" />

<p>
	也可以写进 <code>appsettings.json</code> 的 <code>Ntm</code> 节，但镜像里改文件不方便，推荐用环境变量。改了要重启网关才生效。
</p>

<Heading id="env">环境变量</Heading>

<table>
	<thead>
		<tr><th>名字</th><th>默认</th><th>说明</th></tr>
	</thead>
	<tbody>
		<tr>
			<td><code>NTM_ADMIN_PASSWORD</code></td>
			<td>（随机）</td>
			<td>第一次启动时的管理员密码，至少 12 个字符。库里已经有管理员时不读。不设就随机生成一个，打印在日志里；用生成的密码登录后必须先改密码，才能使用其余管理功能。</td>
		</tr>
		<tr>
			<td><code>NTM_TRUSTED_PROXIES</code></td>
			<td>（无）</td>
			<td>可信的反向代理，IP 或 CIDR，逗号分隔。见<a href={resolve('/deploy') + '#reverse-proxy'}>反向代理</a>。</td>
		</tr>
		<tr>
			<td><code>TZ</code></td>
			<td>UTC（仓库的 compose.yaml 里是 Asia/Shanghai）</td>
			<td>网关时区，决定每日费用上限何时重置、渠道可用时段怎么算。见<a href={resolve('/deploy') + '#timezone'}>时区</a>。</td>
		</tr>
		<tr>
			<td><code>ASPNETCORE_HTTP_PORTS</code></td>
			<td>8080</td>
			<td>监听端口（镜像里的默认值）。</td>
		</tr>
		<tr>
			<td><code>Logging__Console__FormatterName</code></td>
			<td>simple</td>
			<td>日志格式。默认一行一条、带时间；设成 <code>json</code> 就每行一个 JSON 对象，方便交给日志系统。见<a href={resolve('/deploy') + '#logs'}>日志</a>。</td>
		</tr>
	</tbody>
</table>

{#each sections as section (section.id)}
	<Heading id={section.id}>{section.title}</Heading>
	<p>{section.intro}</p>
	<table>
		<thead>
			<tr><th>环境变量</th><th>默认</th><th>说明</th></tr>
		</thead>
		<tbody>
			{#each section.rows as row (row.key)}
				<tr>
					<td><code>{row.key}</code></td>
					<td><code>{row.value}</code></td>
					<td>{row.note}</td>
				</tr>
			{/each}
		</tbody>
	</table>
{/each}

<p class="note">
	网关不再自动创建默认 API 密钥，也不会把它写进启动日志。请登录管理页面后在「API 密钥」页创建；种子渠道不会替你创建 API 密钥。已有部署的 API 密钥不受影响。
</p>
