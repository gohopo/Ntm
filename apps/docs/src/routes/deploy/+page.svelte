<script lang="ts">
	import { resolve } from '$app/paths';
	import { Code, Heading, adr } from '#lib';

	const image = `services:
  ntm:
    image: gohopo/ntm:0.1.0`;

	const build = `services:
  ntm:
    image: gohopo/ntm:0.1.0
    build: .`;

	const backup = `docker compose exec ntm dotnet Ntm.Server.dll backup
# 拷到宿主机上，放到别的盘或别的机器
docker compose cp ntm:/app/data/backups ./ntm-backups`;

	const restore = `docker compose stop ntm
docker compose run --rm ntm restore /app/data/backups/20261106-080000Z
docker compose start ntm`;

	const restoreCopy = `docker compose cp ./ntm-backups/20261106-080000Z ntm:/app/data/backups/`;

	const upgradeImage = `# 先把 compose.yaml 里 image 的版本号改成新版本，再
docker compose exec ntm dotnet Ntm.Server.dll backup
docker compose pull
docker compose up -d`;

	const upgradeSource = `docker compose exec ntm dotnet Ntm.Server.dll backup
git pull
docker compose up -d --build`;

	const healthz = `{"status":"ok","version":"0.1.0","commit":"9fbc013abefa"}`;

	const unhealthy = `{"status":"unhealthy","version":"0.1.0","commit":"9fbc013abefa","failed":["请求记录库"]}`;

	const metricsEnv = `environment:
  Ntm__Metrics__Enabled: "true"
  Ntm__Metrics__Token: "换成一个长的随机字符串"`;

	const scrape = `scrape_configs:
  - job_name: ntm
    authorization:
      credentials: 换成一个长的随机字符串
    static_configs:
      - targets: ["ntm:8080"]`;

	const jsonLogs = `environment:
  Logging__Console__FormatterName: json`;

	const nginx = `location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    # 流式响应可能很久才结束；非流式请求最长等 10 分钟
    proxy_read_timeout 15m;
    # 网关接受的请求体最大 32 MiB（带图片的请求会很大）
    client_max_body_size 32m;
}`;

	const resetDocker = `docker compose exec ntm dotnet Ntm.Server.dll reset-admin-password`;

	const source = `pnpm install
pnpm build:web
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Ntm.Server`;

	const resetSource = `dotnet run --project src/Ntm.Server -- reset-admin-password`;
</script>

<h1>部署与运维</h1>

<p class="lead">
	推荐用仓库里的 <code>compose.yaml</code> 部署。这一页讲数据放在哪、怎么备份恢复和升级、怎么看网关是否健康，以及放在反向代理后面要注意什么。
</p>

<Heading id="docker">Docker Compose</Heading>

<p>
	仓库里的 <code>compose.yaml</code> 用 Docker Hub 上发布的镜像 <code>gohopo/ntm</code>，有 amd64 和 arm64 两种架构。标签是版本号，例如
	<code>0.1.0</code>，另有 <code>0.1</code> 和 <code>latest</code>。<code>compose.yaml</code> 里写死了一个版本号，每次发版都会跟着改成新版本；部署时也请写死版本号，不要用
	<code>latest</code>，免得哪次拉镜像时悄悄升级：
</p>

<Code code={image} label="compose.yaml" />

<p>
	想从源码构建，就在仓库根目录取消 <code>build: .</code> 那一行的注释，执行 <code>docker compose up -d --build</code>，构建出的镜像沿用上面的名字：
</p>

<Code code={build} label="compose.yaml" />

<p>
	每个版本改了什么，看 <a href="https://github.com/gohopo/Ntm/releases">GitHub Releases</a>。容器里网关监听 8080 端口，以 uid
	1654 运行，没有 shell。<code>compose.yaml</code> 里能改的有三个环境变量，没用到的两个默认注释着：
</p>

<ul>
	<li><code>NTM_ADMIN_PASSWORD</code>：第一次启动时的管理员密码，见<a href={resolve('/quick-start')}>快速开始</a>。</li>
	<li><code>TZ</code>：网关时区，见<a href="#timezone">时区</a>。</li>
	<li><code>NTM_TRUSTED_PROXIES</code>：可信的反向代理，见<a href="#reverse-proxy">反向代理</a>。</li>
</ul>

<p>其余配置项见<a href={resolve('/configuration')}>配置参考</a>，一般不用动。</p>

<Heading id="data">数据目录</Heading>

<p>所有数据都在容器的 <code>/app/data</code>，compose 里挂的是命名卷 <code>ntm-data</code>。里面有：</p>

<table>
	<thead>
		<tr><th>文件</th><th>内容</th></tr>
	</thead>
	<tbody>
		<tr><td><code>ntm.db</code></td><td>配置：渠道、渠道密钥（加密）、下发模型、API 密钥（只存指纹）、单价、管理员</td></tr>
		<tr><td><code>ntm-traffic.db</code></td><td>请求记录、每小时汇总、Responses 的落点</td></tr>
		<tr><td><code>ntm-debug.db</code></td><td>调试原文，保留 24 小时</td></tr>
		<tr><td><code>keys/</code></td><td>Data Protection 密钥环，用来解开库里加密的渠道密钥和代理地址</td></tr>
		<tr><td><code>price-catalog.json</code></td><td>价目目录的缓存，丢了会重新下载</td></tr>
	</tbody>
</table>

<p class="warn">
	密钥环和库要一起备份。只恢复库、不恢复 <code>keys/</code> 的话，渠道密钥和代理地址都解不开，得重新填一遍（<a
		href={adr('0006-credentials-at-rest.md')}>ADR-0006</a
	>）。
</p>

<p>
	想改用宿主机目录挂载，先把那个目录 <code>chown 1654:1654</code>。目录必须在本地盘上：SQLite 的 WAL 在 NFS、SMB
	这类网络文件系统上不工作。
</p>

<p>
	一个数据目录只能给一个网关用。网关启动时会锁住目录里的 <code>ntm.lock</code>，第二个用同一个目录的网关会报「数据目录……正被另一个网关使用」并退出（<a
		href={adr('0003-single-instance-in-memory-runtime-state.md')}>ADR-0003</a
	>）。
</p>

<Heading id="backup">备份和恢复</Heading>

<p>
	网关自带 <code>backup</code> 命令，开着网关也能备份。它把配置库和请求记录库各拷一份一致的快照，再拷上密钥环和价目缓存，放进数据目录下的
	<code>backups/时间</code>（UTC 时间，例如 <code>backups/20261106-080000Z</code>）。调试原文库不备份，它 24 小时就过期。
</p>

<Code code={backup} label="备份" />

<p class="warn">
	备份里有密钥环，拿到备份就能解开所有渠道密钥，要像密码一样保管。备份还在数据卷里，卷坏了它也跟着没了，记得拷出去。网关不会自动备份，也不会删旧备份。
</p>

<p>
	恢复要先停掉网关：<code>restore</code> 发现网关还开着就拒绝执行。它先把数据目录里现有的库和密钥环挪进
	<code>before-restore-时间</code> 目录（不删），再把备份拷进来：
</p>

<Code code={restore} label="恢复" />

<p>备份已经拷出去、卷里没有了的话，先把它拷回卷里再恢复：</p>

<Code code={restoreCopy} label="sh" />

<p>
	请求记录默认只留 7 天，库不会无限变大；保留多久见<a href={resolve('/configuration') + '#traffic'}>配置参考</a>。
</p>

<Heading id="upgrade">升级</Heading>

<ol>
	<li>先看 <a href="https://github.com/gohopo/Ntm/releases">Releases</a> 里这个版本有没有「⚠ BREAKING CHANGES」这一节，有的话照它的说明改配置或客户端。</li>
	<li>用 <code>backup</code> 备份一次。</li>
	<li>换成新版本的镜像，启动。</li>
</ol>

<Code code={upgradeImage} label="用发布的镜像" />

<Code code={upgradeSource} label="从源码构建" />

<p>
	库的结构在网关启动时自动迁移，不用手动跑命令。迁移只往前走：新版本启动过一次之后，旧版本就认不出这个库了。这时直接换回旧版本，网关会拒绝启动并提示「……是更新的版本写的」，而不是带着看不懂的库运行。想退回旧版本，就停掉网关，用升级前的备份
	<a href="#backup">restore</a>，再用旧版本启动。
</p>

<Heading id="shutdown">停机</Heading>

<p>
	网关收到停止信号（<code>docker compose stop</code>、<code>docker stop</code>）后不再接新请求，先等进行中的请求最多 30
	秒。还没结束的就中断：没开始回复的客户端收到 503，错误码 <code>gateway_shutting_down</code>，可以重试；回复到一半的流式请求收到一个错误事件。被中断的请求照常记进请求日志。
</p>

<p>
	仓库的 <code>compose.yaml</code> 给了 50 秒的停机时间（<code>stop_grace_period</code>）。不用 compose 时，给
	<code>docker stop</code> 也留够时间，例如 <code>docker stop -t 50</code>；默认的 10 秒不够，网关会在写完记录前被强行杀掉。
</p>

<Heading id="reverse-proxy">反向代理</Heading>

<p>放在 HTTPS 反向代理后面时，把代理的 IP 或 CIDR 写进 <code>NTM_TRUSTED_PROXIES</code>，多个用逗号分隔，例如：</p>

<Code code={`NTM_TRUSTED_PROXIES: "172.16.0.0/12, 10.0.0.1"`} label="compose.yaml" />

<p>
	只有来自这些地址的 <code>X-Forwarded-Proto</code> 和 <code>X-Forwarded-For</code> 才被采信。不设的话，管理员的登录 cookie 不带
	Secure，登录失败的限流也只看得到代理的地址，等于所有人共用一个额度。写错格式时网关拒绝启动。
</p>

<p>
	代理那边要放宽超时和请求体大小。网关在流式响应上已经发了 <code>X-Accel-Buffering: no</code>，nginx 不会缓冲它。nginx 的例子：
</p>

<Code code={nginx} label="nginx" />

<Heading id="timezone">时区</Heading>

<p>
	渠道的「每日费用上限」按网关时区的零点重置，渠道的可用时段也按网关时区算。时区由 <code>TZ</code> 环境变量决定，compose
	里默认是 <code>Asia/Shanghai</code>；不设 <code>TZ</code> 时是 <code>UTC</code>。改了要重启网关才生效。请求日志和统计图在浏览器里按浏览器的时区显示，和
	<code>TZ</code> 无关。
</p>

<Heading id="health">健康检查</Heading>

<p>
	<code>GET /healthz</code> 不需要认证。它在 2 秒内查一下配置库和请求记录库，都正常就回 200 和版本号：
</p>

<Code code={healthz} label="200" />

<p>哪个库查不通就回 503，并写出是哪个：</p>

<Code code={unhealthy} label="503" />

<p>
	它不检查渠道是否可用，渠道的情况看「总览」和渠道页的健康度。镜像里配好了 Docker 的健康检查：每 30 秒在容器里跑一次
	<code>dotnet Ntm.Server.dll healthcheck</code>，它请求本机的 <code>/healthz</code>，200 就算健康。<code>docker compose ps</code>
	里能看到 <code>healthy</code> 或 <code>unhealthy</code>。
</p>

<Heading id="metrics">指标</Heading>

<p>
	网关可以在 <code>/metrics</code> 给 Prometheus 抓指标，默认关。打开并设一个令牌：
</p>

<Code code={metricsEnv} label="compose.yaml" />

<Code code={scrape} label="prometheus.yml" />

<table>
	<thead>
		<tr><th>指标</th><th>说明</th></tr>
	</thead>
	<tbody>
		<tr><td><code>ntm_requests_total</code></td><td>结束的请求数，按下发模型（model）、状态（status）、来源（origin：client 或 workbench）</td></tr>
		<tr><td><code>ntm_attempts_total</code></td><td>发往上游的尝试数，按渠道（channel_id、channel）和失败种类（failure，成功是 none）</td></tr>
		<tr><td><code>ntm_tokens_total</code></td><td>用量，按下发模型和种类（type：input、cache_read、cache_write、output）</td></tr>
		<tr><td><code>ntm_cost_dollars_total</code></td><td>费用（美元，含倍率），按下发模型；无单价的不计</td></tr>
		<tr><td><code>ntm_in_flight_requests</code></td><td>进行中的请求数</td></tr>
		<tr><td><code>ntm_cooldowns</code></td><td>正在生效的冷却条目数</td></tr>
		<tr><td><code>ntm_record_queue_length</code></td><td>还没写进库的请求记录数，一直涨说明写库跟不上</td></tr>
		<tr><td><code>ntm_build_info</code></td><td>版本号和提交，值恒为 1</td></tr>
		<tr><td><code>ntm_start_time_seconds</code></td><td>网关启动的时刻</td></tr>
	</tbody>
</table>

<p>
	计数只在内存里，网关重启后从零算，Prometheus 的 <code>rate()</code> 会自动处理。不按 API 密钥分，想看每个 API 密钥的用量和费用，用「总览」和请求日志。
</p>

<Heading id="logs">日志</Heading>

<p>
	日志写到标准输出，一行一条，开头是时间。<code>docker compose logs ntm</code> 就能看。仓库的 <code>compose.yaml</code>
	限制了日志文件的大小（每个 10 MB，留 5 个）。要交给日志系统的话，可以改成每行一个 JSON：
</p>

<Code code={jsonLogs} label="compose.yaml" />

<Heading id="reset-password">重置管理员密码</Heading>

<p>忘了管理员密码时，在网关所在的环境里跑：</p>

<Code code={resetDocker} label="Docker" />

<p>
	它生成一个新的随机密码，打印出来，只显示这一次；所有已经登录的会话立刻失效。故意不接受从参数传入密码，免得留在 shell 历史里。登录后可以在「设置」里改成自己的密码。
</p>

<Heading id="from-source">从源码运行</Heading>

<p>需要 .NET 10 SDK、Node.js 22.17 以上和 pnpm。在仓库根目录：</p>

<Code code={source} label="sh" />

<p>
	默认监听 <code>http://localhost:5278</code>，数据放在 <code>src/Ntm.Server/data</code>。必须设
	<code>ASPNETCORE_ENVIRONMENT=Development</code>：前端的压缩文件只有发布产物里才有，不设的话页面是空白的。从源码重置密码：
</p>

<Code code={resetSource} label="sh" />
