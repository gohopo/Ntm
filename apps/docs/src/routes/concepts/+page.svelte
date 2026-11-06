<script lang="ts">
	import { resolve } from '$app/paths';
	import { Heading, adr } from '#lib';
</script>

<h1>概念</h1>

<p class="lead">
	网关把客户端请求的<strong>下发模型</strong>，按<strong>匹配规则</strong>对应到若干<strong>渠道</strong>的<strong>上游模型</strong>，再挑一个候选、用它的一把<strong>渠道密钥</strong>发出去。这一页按这条链路依次讲每个概念。
</p>

<Heading id="channel">渠道</Heading>

<p>一个渠道就是一个上游服务：一个地址、一种 API 格式、若干把渠道密钥。在「渠道」页管理，主要的字段：</p>

<ul>
	<li>
		<strong>API 格式</strong>：上游说哪种格式，OpenAI Chat、OpenAI Responses 或 Anthropic。客户端用什么格式来都行，格式不同时网关转换，见<a
			href={resolve('/conversion')}>协议互转</a
		>。嵌入和图片只能走 OpenAI Chat 格式的渠道。
	</li>
	<li>
		<strong>上游地址</strong>和<strong>请求路径（可选）</strong>：路径不填就按格式的默认路径（如 <code>/v1/chat/completions</code>）。地址末尾的
		<code>/v1</code>、<code>/v3</code> 这类版本段会自动拆出来，页面上显示实际请求的 URL。
	</li>
	<li><strong>优先级</strong>和<strong>权重</strong>：见下面的<a href="#routing">选路</a>。</li>
	<li><strong>供应商</strong>和<strong>计费倍率</strong>：按谁的价目表算钱、实际再乘多少，见<a href={resolve('/billing')}>计费</a>。</li>
	<li><strong>代理</strong>：这个渠道走哪个代理，支持 http、https、socks4、socks4a、socks5、socks5h，地址加密保存。</li>
	<li>
		<strong>启用</strong>：停用的渠道不参与选路，但在工作台里仍能测。
	</li>
</ul>

<p>
	渠道页还可以设每日费用上限、可用时段、<a href="#rewrite">请求改写</a>和调试原文，并能看到这个渠道的冷却和健康度。
</p>

<Heading id="rewrite" level={3}>请求改写</Heading>

<p>
	上游要求额外的请求头或请求体字段时，在渠道上加请求改写规则，最多 20 条。每条规则对请求头或 JSON 请求体的一个字段做「设置」（覆盖客户端给的）、「删除」或「补充」（客户端没给才写）。路径用点号分隔，例如
	<code>thinking.type</code>。
</p>

<p>
	规则作用在真正发往上游的请求上，所以字段按上游的格式写：渠道是 Anthropic 格式，就按 Anthropic 的请求体写，不管客户端用什么格式来。认证头、<code
		>Content-Type</code
	>，以及请求体里的 <code>model</code> 和 <code>stream</code> 不能改。有请求体规则的渠道，网关会把请求体解析后重新序列化：字段不会丢，但空白和转义写法可能变。
</p>

<Heading id="channel-key">渠道密钥</Heading>

<p>
	渠道密钥是上游的凭证，一个渠道可以有多把。密钥只写不读：保存后加密存在库里，页面上只显示前缀。每把密钥可以设优先级、每分钟请求数上限、并发上限，以及只用于哪些上游模型。
</p>

<p>
	同一个渠道里优先用优先级高的密钥。某把密钥认证失败、额度用完或被限流，网关换这个渠道的下一把密钥，不换渠道。勾了「密钥属于同一个账号」的渠道，Responses
	的后续请求可以换用同渠道的其他密钥，见<a href={resolve('/conversion') + '#responses-state'}>Responses 的上游状态</a>。
</p>

<Heading id="upstream-model">上游模型和模型同步</Heading>

<p>
	上游模型是渠道那边真实存在的模型名，列在渠道的「上游模型」里。可以手填，也可以同步：勾了「自动同步上游模型清单」的渠道每小时用自己的密钥和代理问一次上游，「立即同步」随时问一次，顺便验证地址、密钥和代理都通。
</p>

<p>
	同步只替换同步来的条目，手填的不动；上游没回清单或回了空清单都算失败，保留原来的列表。同步<strong>不会</strong>把新模型发给客户端：新模型只有正好被某个下发模型的匹配规则选中，才会进入选路。这种进出会记成事件，总览页提示一次。
</p>

<Heading id="published-model">下发模型</Heading>

<p>
	下发模型是客户端看到的模型名，也就是请求里的 <code>model</code>，<code>GET /v1/models</code> 列出的也是它们。在「下发模型」页管理。每个下发模型有一个类型：对话、嵌入或图片，类型决定客户端用哪个接口请求它。名字在所有类型里唯一。
</p>

<p>新建的下发模型默认不下发。确认候选没问题后勾上「下发（客户端能看到并使用）」再保存，客户端才能用。</p>

<Heading id="match-rule">匹配规则</Heading>

<p>下发模型靠匹配规则找到候选。每条规则：</p>

<ul>
	<li>
		<strong>匹配方式</strong>：「精确」或「正则」。正则必须匹配整个上游模型名，不支持回溯引用和环视（<a
			href={adr('0011-explicit-match-kind-nonbacktracking-regex.md')}>ADR-0011</a
		>）。
	</li>
	<li><strong>只用这个渠道</strong>：只在某一个渠道里找；不选就在所有渠道里找。</li>
	<li><strong>优先级</strong>和<strong>权重</strong>（可选）：覆盖渠道自己的优先级和权重，只对这条规则选中的候选生效。</li>
	<li><strong>排除的渠道</strong>和<strong>排除模式</strong>：把某些渠道或某些上游模型名去掉，排除模式按这条规则自己的匹配方式判断。</li>
</ul>

<p>
	规则按顺序先到先得：同一个渠道的同一个上游模型只归第一条匹配到它的规则。例如 <code>gpt-4o</code> 的规则可以是「精确
	<code>gpt-4o</code>」，再加一条「正则 <code>gpt-4o-\d&#123;4&#125;-\d&#123;2&#125;-\d&#123;2&#125;</code>」把带日期的版本也纳进来。下发模型页的「候选预览」实时列出规则选中了哪些候选，以及为什么有些渠道被排除。
</p>

<Heading id="routing">选路</Heading>

<p>一个请求到来时，网关这样挑候选：</p>

<ol>
	<li>找出下发模型的所有候选（渠道 + 上游模型），去掉停用的、格式做不了这个请求的、正在冷却或被限制的。</li>
	<li>按优先级分档，数字大的先用。只要高档还有可用的候选，就不会用低档。</li>
	<li>同一档内按权重做平滑加权轮询，权重再乘上<a href="#health">健康度</a>。</li>
	<li>
		发出去。在第一个带真实输出的事件到达客户端之前失败，就换下一个候选重试（最多 5 次尝试），客户端感觉不到；已经开始输出后再失败，只能把错误告诉客户端。
	</li>
</ol>

<p>
	HTTP 200 但响应体是错误、流里出现明确的限流或额度用尽，也按失败处理。下发模型页的「现在的选路」显示此刻每个候选的状态和预计份额，它只读，不会推进轮询。
</p>

<Heading id="health" level={3}>健康度</Heading>

<p>
	健康度是每个「渠道 + 上游模型」近 30 分钟的成功率，用来调低总出错的候选的份额，最低压到原来的十分之一。样本少于 20 个时按比例打折扣，少量失败不会大幅改变分流。只有上游的问题计入健康度（超时、5xx、连不上、伪装成功的错误等）；认证失败、限流、客户端自己的请求错误不计入。健康度只在内存里，网关重启后从零算。渠道页能看到每个上游模型的健康度。
</p>

<Heading id="affinity" level={3}>亲和</Heading>

<p>
	同一个对话的后续请求优先沿用上次的渠道和渠道密钥，让上游的提示缓存留在同一个账号上。网关从请求体里认出「同一个对话」（同一个 API
	密钥、同一个下发模型、前面的消息一样），不需要客户端做什么。亲和 1 小时没被用到就失效。优先级高于亲和：主力渠道恢复后，对话会回到主力渠道。
</p>

<Heading id="cooldown" level={3}>冷却</Heading>

<p>出错后，网关只冷却出问题的那一层，同一个渠道的其余部分照常服务：</p>

<table>
	<thead>
		<tr><th>错误</th><th>冷却什么</th><th>第一次冷却多久</th></tr>
	</thead>
	<tbody>
		<tr><td>认证失败</td><td>这把渠道密钥</td><td>5 分钟</td></tr>
		<tr><td>额度用尽</td><td>这把渠道密钥</td><td>30 分钟</td></tr>
		<tr><td>限流</td><td>这把渠道密钥的这个上游模型</td><td>1 分钟</td></tr>
		<tr><td>模型不存在</td><td>这个渠道的这个上游模型</td><td>30 分钟</td></tr>
		<tr><td>超时、5xx、上游过载等</td><td>这个渠道的这个上游模型</td><td>1–2 分钟</td></tr>
		<tr><td>连不上、代理失败</td><td>整个渠道</td><td>2 分钟</td></tr>
		<tr><td>客户端的请求本身有错</td><td>不冷却，错误直接告诉客户端</td><td>—</td></tr>
	</tbody>
</table>

<p>
	连续失败时冷却时间翻倍，最长 30 分钟；上游给了明确的恢复时间（如 <code>Retry-After</code>）就以它为准。冷却到期后先放一个请求去试探，成功就恢复。一个渠道的渠道密钥全在冷却时，这个渠道等于退出了选路。渠道页的「冷却」一节可以手动解除。
</p>

<Heading id="restriction" level={3}>限制</Heading>

<p>
	限制是你主动设的门槛，碰到了就让候选暂时退出选路，但不算失败，不触发冷却：渠道的每日费用上限和可用时段，渠道密钥的每分钟请求数、并发上限和模型白名单。「每日」和可用时段按网关时区算，见<a
		href={resolve('/deploy') + '#timezone'}>时区</a
	>。请求详情会记下哪些候选因为限制被跳过。
</p>

<Heading id="api-key">API 密钥</Heading>

<p>
	API 密钥是发给客户端的凭证，在「API 密钥」页创建。完整的密钥只在创建时显示一次，库里只存指纹。上游的凭证留在网关里，客户端拿不到。每个
	API 密钥可以设：
</p>

<ul>
	<li><strong>费用上限</strong>：终身累计，用完就拒绝。</li>
	<li><strong>并发上限</strong>：同时进行的请求数。</li>
	<li><strong>模型</strong>：只能用哪些下发模型。</li>
	<li><strong>渠道</strong>：只用或不用哪些渠道。</li>
</ul>

<p>管理员只有一个，用密码登录管理页面；管理页面不能用 API 密钥登录。</p>
