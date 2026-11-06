<script lang="ts">
	import { resolve } from '$app/paths';
	import { Heading, adr } from '#lib';
</script>

<h1>南天门</h1>

<p class="lead">
	南天门（Ntm）是一个自托管的 AI 聚合网关。把多个供应商的渠道接在后面，对客户端提供 OpenAI Chat、OpenAI
	Responses 和 Anthropic 三种格式的接口，客户端用哪种格式都行，网关负责选渠道、换格式、失败时切换和记账。
</p>

<Heading id="features">能做什么</Heading>

<ul>
	<li><strong>多渠道聚合</strong>：一个下发模型可以对应多个渠道的多个上游模型，渠道可以是原厂，也可以是中转。</li>
	<li>
		<strong>协议互转</strong>：入站和上游的格式可以任意组合，流式和非流式都转；格式相同时原样透传，只换
		<code>model</code>。细则见<a href={resolve('/conversion')}>协议互转</a>。
	</li>
	<li>
		<strong>选路和容错</strong>：按优先级分档，同档按权重和健康度分流；上游在第一个真正的输出到达客户端之前失败，就悄悄换下一个候选。失败按密钥、模型、渠道分别冷却，只停掉出问题的那一层。
	</li>
	<li><strong>模型同步</strong>：定时向渠道拉取上游模型清单，新模型被已有规则选中时自动进入选路。</li>
	<li>
		<strong>计费和限制</strong>：按 models.dev 的价目或手填的单价算每个请求的费用；API
		密钥可以限费用、模型、渠道和并发，渠道可以限每日费用、每分钟请求数、并发和可用时段。
	</li>
	<li>
		<strong>可观测</strong>：实时看进行中的请求，可以中断；请求日志记下每次尝试落在哪、花了多少；需要时录下请求和响应的原文。
	</li>
	<li><strong>工作台</strong>：在管理页面里按渠道、按模型或用对话测试，不用另配客户端。</li>
	<li>嵌入（<code>/v1/embeddings</code>）和图片生成（<code>/v1/images/generations</code>）在 OpenAI 格式的渠道之间透传。</li>
</ul>

<Heading id="endpoints">客户端可用的接口</Heading>

<table>
	<thead>
		<tr><th>接口</th><th>格式</th></tr>
	</thead>
	<tbody>
		<tr><td><code>POST /v1/chat/completions</code></td><td>OpenAI Chat</td></tr>
		<tr>
			<td><code>POST /v1/responses</code> 及按 id 取回、删除、取消，<code>/v1/conversations</code></td>
			<td>OpenAI Responses</td>
		</tr>
		<tr><td><code>POST /v1/messages</code>、<code>POST /v1/messages/count_tokens</code></td><td>Anthropic</td></tr>
		<tr><td><code>POST /v1/embeddings</code></td><td>OpenAI 嵌入</td></tr>
		<tr><td><code>POST /v1/images/generations</code></td><td>OpenAI 图片生成</td></tr>
		<tr><td><code>GET /v1/models</code></td><td>OpenAI 或 Anthropic 的模型列表</td></tr>
	</tbody>
</table>

<p>客户端用网关发的 API 密钥认证，上游的密钥只留在网关里。怎么配各种客户端见<a href={resolve('/clients')}>客户端接入</a>。</p>

<Heading id="limits">不做什么</Heading>

<ul>
	<li>
		只跑一个实例。冷却、健康度、每分钟请求数和并发都记在进程内存里，不能多开几个实例分担流量（<a
			href={adr('0003-single-instance-in-memory-runtime-state.md')}>ADR-0003</a
		>）。
	</li>
	<li>不支持音频和重排接口。</li>
	<li>只有一个管理员账号，没有多用户和角色。</li>
</ul>

<Heading id="next">接下来</Heading>

<ul>
	<li>第一次用：<a href={resolve('/quick-start')}>快速开始</a>，十分钟内发出第一个请求。</li>
	<li>正式部署：<a href={resolve('/deploy')}>部署与运维</a>和<a href={resolve('/configuration')}>配置参考</a>。</li>
	<li>想弄清渠道、下发模型和选路怎么配合：<a href={resolve('/concepts')}>概念</a>。</li>
</ul>
