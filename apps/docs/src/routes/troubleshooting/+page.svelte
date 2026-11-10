<script lang="ts">
	import { resolve } from '$app/paths';
	import { Code, Heading } from '#lib';

	const logs = `docker compose logs --tail 200 ntm`;
</script>

<h1>排错</h1>

<p class="lead">
	先看「请求日志」：几乎所有经过网关的请求都有记录，请求详情写明每次尝试落在哪里、为什么失败、哪些候选被跳过。下面按客户端看到的错误列出常见原因。
</p>

<Heading id="first-steps">先看哪里</Heading>

<ul>
	<li><strong>请求日志和请求详情</strong>：每次尝试的渠道、上游模型、渠道密钥、HTTP 状态和上游的错误信息。</li>
	<li>
		<strong>下发模型页的「现在的选路」</strong>：此刻每个候选可不可用，为什么不可用。「候选预览」：规则到底选中了哪些候选。
	</li>
	<li><strong>渠道页的「冷却」和「健康度」</strong>：哪一层在冷却，什么时候到期，可以手动解除。</li>
	<li>
		<strong>调试原文</strong>：上游到底回了什么。打开方法见<a href={resolve('/observability') + '#debug-capture'}>调试原文</a>。
	</li>
	<li><strong>服务日志</strong>：</li>
</ul>

<Code code={logs} label="sh" />

<Heading id="401">401：API 密钥无效</Heading>

<ul>
	<li>「缺少 API 密钥。」：请求里既没有 <code>Authorization: Bearer</code>，也没有 <code>x-api-key</code>。</li>
	<li>「API 密钥无效或已过期。」：密钥写错了，或者这个 API 密钥已经停用、删除或到期。完整的密钥只在创建时显示一次，丢了只能新建一个。</li>
</ul>

<p>上游拒绝渠道密钥时，网关不会把 401 转给客户端，而是换下一把渠道密钥或下一个候选；都失败了客户端看到 502。</p>

<Heading id="403">403：不允许</Heading>

<ul>
	<li>「这个 API 密钥不允许使用模型 …」：API 密钥详情里的「模型」没勾这个下发模型。</li>
	<li>请求被管理员在请求日志里中断，也是 403。</li>
</ul>

<Heading id="404">404：找不到</Heading>

<ul>
	<li>
		「模型 … 没有下发。」：没有这个名字的下发模型，或者有但没勾「下发（客户端能看到并使用）」。注意 <code>model</code> 要写下发模型的名字，不是上游模型名。
	</li>
	<li>「模型 … 是嵌入模型，请改用 …」：下发模型的类型和接口对不上，按提示换接口。</li>
	<li>地址拼错（比如多了或少了 <code>/v1</code>），见<a href={resolve('/clients') + '#basics'}>客户端接入</a>。这种请求没有记录。</li>
</ul>

<Heading id="400">400：请求有问题</Heading>

<ul>
	<li>
		上游认为请求本身有错（参数不合法、上下文太长等）时，网关不换候选，把上游的错误信息原样转给客户端，请求详情里的结果是「请求本身有误」。
	</li>
	<li>
		「这个 previous_response_id 在本网关上已经失效」：落点过了保留期，或者不是这个 API 密钥产出的。请客户端重发完整上下文，见<a
			href={resolve('/conversion') + '#responses-state'}>Responses 的上游状态</a
		>。
	</li>
</ul>

<Heading id="429">429：额度或并发</Heading>

<ul>
	<li>「API 密钥的费用上限已用完。」：在 API 密钥详情里调高或清空「费用上限」。</li>
	<li>「这个 API 密钥同时进行的请求太多了。」：API 密钥的「并发上限」。</li>
	<li>「所有候选渠道都被限流。」：上游都在限流，稍后再试，或者加渠道、加渠道密钥。</li>
</ul>

<Heading id="503">503：没有可用的渠道</Heading>

<p>503 说明网关一个候选都没发出去，消息里写了原因：</p>

<ul>
	<li>
		「模型 … 没有可用的渠道。」：匹配规则没选中任何候选，或者选中的渠道都停用了、没有启用的渠道密钥。看「候选预览」。
	</li>
	<li>
		「模型 … 的候选都在冷却，请 N 秒后重试。」：响应带 <code>Retry-After</code>。看渠道页的「冷却」，确认上游恢复了可以手动解除。
	</li>
	<li>「模型 … 的候选渠道现在都受限制。」：每日费用上限、可用时段、RPM、并发或模型白名单挡住了所有候选。</li>
	<li>
		「网关正在关闭，这个请求被中断了，请重试。」：错误码 <code>gateway_shutting_down</code>，网关停机时还没结束的请求，重试就行，见<a
			href={resolve('/deploy') + '#shutdown'}>停机</a
		>。
	</li>
	<li>
		「模型 … 没有能接这个请求的渠道（…）」：请求带着候选的格式表达不了的参数，括号里写着是哪个，见<a href={resolve('/conversion') + '#route-around'}
			>绕开</a
		>。
	</li>
</ul>

<Heading id="502">502 和 504：上游失败</Heading>

<ul>
	<li>「所有候选渠道都失败了。」：每个候选都试过了。请求详情里有每次尝试的上游状态和错误信息。</li>
	<li>「上游响应超时。」（504）：第一个事件 60 秒没来，或者流中途 5 分钟没有新数据。图片的第一个事件最多等 5 分钟。</li>
	<li>
		渠道连不上：检查上游地址和代理；渠道页的「立即同步」可以当连通性测试，工作台的渠道测试能看到完整的上游响应。
	</li>
</ul>

<Heading id="startup">网关起不来</Heading>

<p>网关退出时会在日志里写一行原因：</p>

<ul>
	<li>
		「数据目录 … 正被另一个网关使用（ntm.lock 被占用）」：同一个数据目录上已经有一个网关在跑。一个数据目录只能给一个网关用，先停掉另一个。
	</li>
	<li>
		「… 是更新的版本写的，这个版本的网关不认识它的迁移」：库已经被新版本迁移过，旧版本不能用它。换回新版本，或者停掉网关、用升级前的备份恢复后再用旧版本，见<a
			href={resolve('/deploy') + '#upgrade'}>升级</a
		>。
	</li>
	<li><code>NTM_TRUSTED_PROXIES</code> 格式不对、<code>Ntm__Seed__BaseUrl</code> 不是 http 或 https 地址：按日志里的提示改了再启动。</li>
</ul>

<Heading id="admin-page">管理页面打不开</Heading>

<ul>
	<li>
		页面只显示「管理页面没有构建」：从源码运行时没先构建前端。在仓库根目录运行 <code>pnpm build:web</code>，再重启 Ntm.Server。Docker 镜像里已经构建好了。
	</li>
	<li>登录后提示必须改密码：现在用的是网关生成或重置的密码，先按提示改成自己的密码；这不是渠道或 API 密钥失效。</li>
	<li>管理操作返回 403「跨站请求被拒绝」：直接从管理页面操作，并检查反代是否保留外部 Host 和端口；返回 415 时检查请求体是否使用 <code>application/json</code>。</li>
	<li>登录或改密码返回 429：按 <code>Retry-After</code> 等待，别连续重试。每个来源或全局失败次数都可能触发限制；已有会话不会因此失效。</li>
	<li>忘了管理员密码：用 <code>reset-admin-password</code> 命令重置，见<a href={resolve('/deploy') + '#reset-password'}>重置管理员密码</a>。</li>
	<li>
		经反向代理访问时登录后马上又要登录，或者登录被限流：检查 <code>NTM_TRUSTED_PROXIES</code>，见<a href={resolve('/deploy') + '#reverse-proxy'}
			>反向代理</a
		>。
	</li>
</ul>

<Heading id="keyring">渠道密钥都解不开</Heading>

<p>
	服务日志里出现「渠道 … 的密钥 … 解不开，可能是 Data Protection 密钥环变了」，说明数据目录里的 <code>keys/</code> 丢了或换了：渠道密钥和代理地址是用它加密的。
</p>

<ul>
	<li>从备份恢复整个数据目录，<code>keys/</code> 和数据库必须来自同一份备份。</li>
	<li>没有备份就只能在渠道页重新填每把渠道密钥和代理。API 密钥只存指纹，不受影响。</li>
</ul>

<p>备份方法见<a href={resolve('/deploy') + '#backup'}>备份和恢复</a>。</p>
