<script lang="ts">
	import { resolve } from '$app/paths';
	import { Heading, adr } from '#lib';
</script>

<h1>计费</h1>

<p class="lead">
	网关按上游返回的用量和原厂单价算出每次请求的费用，再乘上渠道的倍率。费用用来做统计，也用来执行渠道的每日费用上限和 API
	密钥的费用上限。金额以美元计。
</p>

<Heading id="formula">怎么算</Heading>

<p>一次尝试的费用 =（各部分 Token 数 × 对应单价之和）× 渠道的计费倍率。</p>

<ul>
	<li>
		<strong>Token 数</strong>：分成输入、缓存读、缓存写 5 分钟、缓存写 1 小时、输出五部分，取自上游的用量。上游没给用量时（比如客户端中途断开），网关按字符数估算，请求详情里标「网关估算」。
	</li>
	<li><strong>单价</strong>：来自「单价」页，单位是美元 / 每百万 Token，填的是原厂的官方价。</li>
	<li>
		<strong>计费倍率</strong>：渠道上的「计费倍率」。经中转用原厂模型时，供应商填原厂，差价交给倍率：比如中转打八折就填 0.8。
	</li>
</ul>

<p>一个请求可能尝试了好几次，每次实际产生了用量的尝试都会计费，请求的费用是它们的和。</p>

<Heading id="prices">单价页</Heading>

<p>
	「单价」页每个「供应商 + 上游模型」一行，行来自所有渠道声明的供应商和上游模型。上游模型删了，行还留着，免得人工填的单价丢掉。每行有这些列：
</p>

<ul>
	<li>输入、缓存读、缓存写 5 分钟、缓存写 1 小时、输出。</li>
	<li>
		思考输出：有的模型（例如阿里云的部分模型）这次只要输出了思考过程，整个输出都按另一档价格算。留空表示和输出价相同（<a
			href={adr('0013-thinking-mode-output-price.md')}>ADR-0013</a
		>）。
	</li>
	<li>长上下文：一次请求的总输入超过门槛时，整次（包括输出）都按那一组单价算。</li>
	<li>每张图片：见下面的<a href="#embeddings-images">嵌入和图片</a>。</li>
</ul>

<Heading id="catalog" level={3}>自动单价</Heading>

<p>
	网关定期从 <a href="https://models.dev">models.dev</a> 下载价目目录（默认每 24 小时一次，「单价」页也能「立即刷新」），按渠道的「供应商」去配上游模型名：
</p>

<ul>
	<li>名字按字母和数字以外的字符切段，所以 <code>claude-sonnet-4.5</code> 和 <code>claude-sonnet-4-5</code> 算同一个名字。</li>
	<li>目录里某个模型的段要连续出现在上游模型名里才算配上，段数最多的胜出。</li>
	<li>并列且单价不同时记为「无单价」，把候选列出来，让你自己选。</li>
	<li>不做别名：<code>deepseek-chat</code> 这类名字配不上，要人工填。</li>
</ul>

<p>
	渠道的供应商选「不在列表里」时没有自动单价。下载目录不走渠道的代理，也不读 <code>HTTP_PROXY</code>；网关连不上 models.dev 时用上次下载的缓存。地址和刷新间隔见<a
		href={resolve('/configuration') + '#pricing'}>配置参考</a
	>。
</p>

<Heading id="manual" level={3}>人工单价</Heading>

<p>
	在「单价」页编辑一行并保存，这一行就变成「人工」，之后刷新目录不会再改它。「恢复自动」把它改回按目录匹配。改单价只影响之后的请求，已经记下的费用不变。
</p>

<Heading id="unpriced">无单价</Heading>

<p>
	有用量、但缺了需要的单价时，这次请求的费用记为「无单价」，不是 0。无单价的请求不计入渠道的每日费用上限和 API 密钥的费用上限，统计里单独计数。「单价」页可以筛出近 7
	天有无单价请求的行，填上单价后之后的请求就会计费。
</p>

<p class="warn">
	用了费用上限的话，记得给所有在用的上游模型填好单价，否则这些请求不受上限约束。
</p>

<Heading id="embeddings-images">嵌入和图片</Heading>

<ul>
	<li>嵌入按输入 Token 计费。上游没给用量时，网关按输入字符估算。</li>
	<li>
		图片要在「单价」页给上游模型填「每张图片」的单价（美元 / 张，只能人工填），按上游返回的张数乘单价再乘倍率，不看 Token。尺寸和质量不分档。
	</li>
	<li>没填每张图片的单价时按 Token 单价算；Token 单价也没有、或者上游没给用量，就是无单价。</li>
</ul>

<Heading id="limits">费用上限</Heading>

<ul>
	<li>
		<strong>渠道的每日费用上限</strong>：在渠道页的「限制」里设，按倍率后的实际费用算，到了就让这个渠道退出选路到第二天。「每日」按网关时区算，见<a
			href={resolve('/deploy') + '#timezone'}>时区</a
		>。
	</li>
	<li><strong>API 密钥的费用上限</strong>：终身累计，到了就拒绝这个 API 密钥的请求。</li>
</ul>

<p>上限按已经完成的请求算，正在进行的请求不会被打断，所以最终花费可能略超上限。</p>

<Heading id="not-counted">不计入的部分</Heading>

<ul>
	<li>联网搜索、生图这类按次收费的工具费，网关不知道上游怎么收，没有算进费用。</li>
	<li>
		<code>count_tokens</code>、<code>input_tokens</code> 和 Responses 按 id 的接口不计费。后台模式的请求在终态时补记费用，见<a
			href={resolve('/conversion') + '#responses-state'}>Responses 的上游状态</a
		>。
	</li>
	<li>
		工作台的请求照常计费，计入渠道的每日费用，但不属于任何 API 密钥，见<a href={resolve('/observability') + '#workbench'}>工作台</a>。
	</li>
	<li>被中断的请求，已经产生的用量照样计费。</li>
</ul>
