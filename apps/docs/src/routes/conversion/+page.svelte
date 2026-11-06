<script lang="ts">
	import { resolve } from '$app/paths';
	import { Heading, adr } from '#lib';
</script>

<h1>协议互转</h1>

<p class="lead">
	网关认三种 API 格式：OpenAI Chat（<code>/v1/chat/completions</code>）、OpenAI Responses（<code>/v1/responses</code>）和
	Anthropic（<code>/v1/messages</code>）。客户端用哪种格式来，和渠道是哪种格式无关，九种组合都能用，流式和非流式都转。这一页讲转换时网关做了什么、哪些东西转不过去。
</p>

<Heading id="passthrough">透传</Heading>

<p>
	客户端和渠道的格式相同时不转换：网关只把 <code>model</code> 换成上游模型名，其余字节原样转发，响应也原样返回。渠道有<a
		href={resolve('/concepts') + '#rewrite'}>请求改写规则</a
	>时例外，见那一节。
</p>

<ul>
	<li>
		Anthropic 透传时，客户端的 <code>anthropic-version</code> 和 <code>anthropic-beta</code> 请求头照转；其他组合用默认版本，不带 beta 头。
	</li>
	<li>
		Anthropic 渠道默认把密钥放在 <code>x-api-key</code>。有的中转只认 <code>Authorization: Bearer</code>，在渠道上勾「用 Authorization:
		Bearer 带密钥」，两个头不会同时带。
	</li>
</ul>

<Heading id="route-around">表达不了的参数：绕开</Heading>

<p>
	请求带着某种上游格式表达不了的参数时，网关不转那种格式的渠道，改用别的候选；所有候选都绕开了，返回 503 并说明是哪个参数（<a
		href={adr('0014-route-around-unrepresentable-requests.md')}>ADR-0014</a
	>）。
</p>

<table>
	<thead>
		<tr><th>上游格式</th><th>会绕开的请求</th></tr>
	</thead>
	<tbody>
		<tr>
			<td>Anthropic</td>
			<td><code>n</code> 大于 1、<code>logprobs</code>、<code>response_format</code> 要求 JSON、音频输出</td>
		</tr>
		<tr>
			<td>Responses</td>
			<td><code>n</code> 大于 1、停止序列（<code>stop</code> / <code>stop_sequences</code>）、音频输出</td>
		</tr>
		<tr><td>非 Chat</td><td>Chat 的内置搜索 <code>web_search_options</code></td></tr>
		<tr>
			<td>非 Anthropic</td>
			<td>
				Anthropic 的服务端工具（<code>web_search_20250305</code> 这类没有 <code>input_schema</code> 的 tools），以及历史里的
				<code>server_tool_use</code>、<code>web_search_tool_result</code> 等块
			</td>
		</tr>
		<tr>
			<td>非 Responses</td>
			<td>
				Responses 的内置工具（联网搜索、文件搜索、代码解释器等）、<code>file_id</code> 引用的图片和文件、<code>previous_response_id</code>、<code
					>conversation</code
				>、<code>background</code>、<code>compact</code>
			</td>
		</tr>
	</tbody>
</table>

<p>只影响采样的参数（<code>seed</code>、<code>frequency_penalty</code> 之类）在目标格式没有时直接丢掉，不绕开。</p>

<Heading id="to-anthropic">转到 Anthropic 上游</Heading>

<Heading id="max-tokens" level={3}>max_tokens</Heading>

<p>
	Anthropic 要求必须给 <code>max_tokens</code>。客户端没给时网关补 4096（开了思考时另算，见下）。要更多，给渠道加一条「补充
	<code>max_tokens</code>」的请求改写规则。
</p>

<Heading id="thinking" level={3}>推理强度和思考</Heading>

<p>
	客户端的推理强度（Chat 的 <code>reasoning_effort</code>、Responses 的 <code>reasoning.effort</code>）会换成 Anthropic 的
	<code>thinking</code>。客户端没给强度时，网关不替它开思考。渠道上的「思考参数写法」决定怎么写：
</p>

<ul>
	<li>
		<strong>自动</strong>（默认）：上游模型名是 Claude 4.6 及以后的，用自适应写法；其余，包括第三方模型，用预算写法。也认
		<code>opus-4-7</code> 这样不带 <code>claude-</code> 的名字。
	</li>
	<li>
		<strong>预算</strong>：写 <code>thinking.type: "enabled"</code> 加 <code>budget_tokens</code>。档位：minimal 1024、low 2048、medium
		8192、high 24576、xhigh 和 max 32768。
	</li>
	<li>
		<strong>自适应</strong>：写 <code>thinking.type: "adaptive"</code> 加 <code>output_config.effort</code>，并加
		<code>display: "summarized"</code>，否则 Claude 5 起只回签名、不回思考文字。
	</li>
</ul>

<p>细节：</p>

<ul>
	<li>
		客户端给了 <code>max_tokens</code> 就不调大，预算让出 1024 给回答，剩下不够 1024 就不开思考。没给时，预算写法补「预算加 4096」，自适应写法补
		32768。
	</li>
	<li>
		开了思考就去掉 <code>temperature</code>，<code>top_p</code> 只保留 0.95 到 1 之间的值；自适应写法一律不发 <code>temperature</code> 和
		<code>top_p</code>。
	</li>
	<li><code>none</code> 在预算写法下什么都不写，在自适应写法下写 effort low。</li>
	<li>
		预算写法下，客户端强制调用工具，或者正处在工具循环中途（OpenAI 的客户端传不回思考的签名），这一次不开思考，否则 Anthropic 会回 400。
	</li>
	<li>
		「自动」猜错时（上游回「thinking.type.enabled is not supported」或「adaptive thinking is not supported」），网关用同一把密钥换另一种写法重发一次，并按渠道和上游模型记住，重启后重新学。
	</li>
</ul>

<p>原因和取舍见 <a href={adr('0021-reasoning-effort-and-thinking-conversion.md')}>ADR-0021</a>。</p>

<Heading id="cache-breakpoints" level={3}>提示缓存断点</Heading>

<p>
	OpenAI 客户端不会写 Anthropic 的 <code>cache_control</code>，网关自动加，最多 4 个，都是 5 分钟：最后一个 tool、最后一个 system
	块、最后一条消息、前一条 user 消息。这样多轮对话的前缀能命中缓存，命中部分按缓存读计费，便宜很多。
</p>

<ul>
	<li>客户端自己在 content part 上写了 <code>cache_control</code>（OpenRouter 的写法）就照它的，不再自动加。</li>
	<li>超过 4 个，或者 1 小时的排在 5 分钟后面，网关会整理成 Anthropic 接受的样子。</li>
	<li>渠道上可以关掉「自动加缓存断点」，关掉后客户端写的照转。</li>
	<li>请求改写规则会写进去的断点（顶层 <code>cache_control</code>，或规则值里的 <code>cache_control</code>）每个占一个位置，网关少放这么多。</li>
	<li>第三方的 Anthropic 兼容上游不一定认：阿里云百炼按断点走显式缓存、按显式缓存计费；DeepSeek、Kimi 忽略断点。</li>
	<li>Anthropic 客户端透传时，网关不加也不改断点。</li>
</ul>

<p>见 <a href={adr('0019-anthropic-cache-breakpoints-and-count-tokens.md')}>ADR-0019</a>。</p>

<Heading id="count-tokens" level={3}>count_tokens</Heading>

<p>
	<code>POST /v1/messages/count_tokens</code> 转给这个模型第一个 Anthropic 格式的候选渠道，只带计数需要的字段。上游不支持（404 或 405，记住 24
	小时）、失败或超时（10 秒），或者根本没有 Anthropic 渠道，就在网关本地估算，估算值往高处偏 10%。响应头 <code>x-ntm-token-count</code> 写明是
	<code>upstream</code> 还是 <code>estimated</code>。这个接口不花钱，不写请求日志，也不受频率、并发、费用上限和可用时段限制。
</p>

<Heading id="from-anthropic">Anthropic 客户端转到 OpenAI 上游</Heading>

<ul>
	<li>
		<code>output_config.effort</code> 照原文写成推理强度。只有 <code>budget_tokens</code> 时分三档：不超过 4096 是 low，不超过 16384 是
		medium，更大是 high。
	</li>
	<li><code>thinking.type: "disabled"</code> 不写强度，上游按它的默认强度。</li>
	<li>
		不加 <code>reasoning.summary</code>（没过组织验证的 OpenAI 账号会 400），所以看不到思考内容；需要时给渠道加一条请求改写规则。
	</li>
</ul>

<Heading id="to-responses">转到 Responses 上游</Heading>

<p>
	网关固定发 <code>store: false</code>，并要回加密的推理内容，下一轮能把推理原样带回去。同一段对话会带同一个
	<code>prompt_cache_key</code>，落在同一份提示缓存上。
</p>

<Heading id="responses-state">Responses 的上游状态</Heading>

<p>
	<code>previous_response_id</code>、<code>conversation</code>、后台模式和按 id 的接口依赖上游保存的状态，只有 Responses
	格式的渠道能做，而且必须回到当初产出它的那个渠道（<a href={adr('0020-responses-landings-and-by-id-endpoints.md')}>ADR-0020</a>）。
</p>

<ul>
	<li>
		<strong>落点</strong>：网关记下每个响应 id、对话 id 是哪个渠道、哪把渠道密钥产出的，存在库里保留 30 天，重启不丢。保留期见<a
			href={resolve('/configuration') + '#traffic'}>配置参考</a
		>。
	</li>
	<li>
		<code>previous_response_id</code> 和 <code>conversation</code> 只发回落点的渠道和渠道密钥，不换候选。过了保留期或落点正在冷却，返回 400，请客户端重发完整上下文或重新建对话。两者只能用一个；<code
			>item_reference</code
		> 要和其中一个一起用。
	</li>
	<li>
		渠道勾了「密钥属于同一个账号」时只固定渠道：产出它的那把密钥用不了，就换这个渠道的其他密钥。只有这些密钥真的属于同一个上游账号时才勾，否则上游找不到那个响应。
	</li>
	<li>
		<code>background</code> 只发 Responses 上游。排队时还没有用量，到了终态网关把用量和费用补进原来那条请求记录：客户端按 id 取回时触发；客户端不来取，网关自己去拉，30
		秒后第一次，之后每分钟，满 10 分钟后每 10 分钟，满 1 小时后每 30 分钟，上游 404 或满 24 小时就放弃。
	</li>
	<li>
		按 id 的接口：<code>GET</code> 和 <code>DELETE /v1/responses/&#123;id&#125;</code>、<code>POST /v1/responses/&#123;id&#125;/cancel</code>、<code
			>GET /v1/responses/&#123;id&#125;/input_items</code
		>、<code>/v1/conversations</code> 的增删改查和条目。它们只发往落点的那把渠道密钥，不选路、不重试，也不看冷却、费用上限、时段、RPM 和并发。只认本网关、用同一个 API
		密钥产出的 id，别的一律 404；落点的渠道或密钥用不了时回 502。取回支持 <code>?stream=true&amp;starting_after=N</code> 续流。这些请求不进请求日志，删除和取消只写服务日志。
	</li>
	<li>
		<code>POST /v1/responses/input_tokens</code> 带 <code>previous_response_id</code> 或 <code>conversation</code> 时回到落点，否则发给第一个
		Responses 候选，只试一次，不写记录。<code>POST /v1/responses/compact</code> 照常选路和记账。
	</li>
</ul>

<Heading id="reasoning">推理内容</Heading>

<p>
	推理内容的签名（Anthropic 的 <code>signature</code>、Responses 的 <code>encrypted_content</code>）只带回产出它的那种格式；换格式时只保留推理文字。输出文字里的引用标注（annotations）只在透传时保留。
</p>

<Heading id="embeddings-images">嵌入和图片</Heading>

<p>
	嵌入（<code>/v1/embeddings</code>）和图片生成（<code>/v1/images/generations</code>）不转换，只在 OpenAI Chat 格式的渠道之间透传：请求体原样转发，只换
	<code>model</code>（<a href={adr('0018-embeddings-and-images-passthrough.md')}>ADR-0018</a>）。
</p>

<ul>
	<li>下发模型的类型要选「嵌入」或「图片」。用错接口返回 404，并指出该用哪个接口。</li>
	<li>
		上游地址从渠道的请求路径推出：没填路径用默认端点；路径以 <code>/chat/completions</code> 结尾，就把结尾换成 <code>/embeddings</code> 或
		<code>/images/generations</code>；别的自定义路径推不出来，这个渠道不会成为这两类请求的候选。
	</li>
	<li>渠道的请求体改写规则不作用在它们上，请求头规则照常生效。</li>
	<li>上游返回空结果算失败，换候选。</li>
	<li>图片支持流式透传。图片的第一个事件最多等 5 分钟，超时不换候选，因为上游可能已经在出图、已经在扣钱。</li>
	<li>计费见<a href={resolve('/billing') + '#embeddings-images'}>计费</a>。</li>
</ul>
