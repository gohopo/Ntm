<script lang="ts">
	import { resolve } from '$app/paths';
	import { Code, Heading } from '#lib';

	const download = `mkdir ntm && cd ntm
curl -O https://raw.githubusercontent.com/gohopo/Ntm/main/compose.yaml`;

	const password = `    environment:
      TZ: Asia/Shanghai
      NTM_ADMIN_PASSWORD: "换成你自己的密码"`;

	const up = `docker compose up -d`;

	const logs = `docker compose logs ntm`;

	const chat = `curl http://localhost:8080/v1/chat/completions \\
  -H "Authorization: Bearer 你的 API 密钥" \\
  -H "Content-Type: application/json" \\
  -d '{"model": "gpt-4o", "messages": [{"role": "user", "content": "你好"}]}'`;

	const models = `curl http://localhost:8080/v1/models \\
  -H "Authorization: Bearer 你的 API 密钥"`;
</script>

<h1>快速开始</h1>

<p class="lead">
	用 Docker Compose 起一个网关，接一个渠道，发出第一个请求。下面的例子假设上游是一个 OpenAI 格式的服务，模型叫
	<code>gpt-4o</code>，按你手上的换掉即可。
</p>

<Heading id="start">1. 启动</Heading>

<p>需要 Docker 和 Compose 插件。不用取代码，只要仓库里的 <code>compose.yaml</code>，它用的是 Docker Hub 上发布的镜像 <code>gohopo/ntm</code>：</p>

<Code code={download} label="sh" />

<p>
	打开 <code>compose.yaml</code>，去掉 <code>NTM_ADMIN_PASSWORD</code> 那一行的注释，改成你的管理员密码，至少 12 个字符。它只在第一次启动、库里还没有管理员时生效，之后改密码在管理页面的「设置」里改。
</p>

<Code code={password} label="compose.yaml" />

<p>然后启动：</p>

<Code code={up} label="sh" />

<p>不设的话网关随机生成一个密码，打印在启动日志里，只显示这一次：</p>

<Code code={logs} label="sh" />

<p>
	启动日志不会生成或显示默认 API 密钥，请按第 5 步在管理页面创建。忘了管理员密码怎么办见<a
		href={resolve('/deploy') + '#reset-password'}>重置管理员密码</a
	>。
</p>

<Heading id="login">2. 登录</Heading>

<p>打开 <code>http://localhost:8080</code>，用管理员密码登录。如果密码是网关随机生成的，登录后必须先改成自己的密码，才能创建渠道等；新密码至少 12 个字符，不能继续用那个生成的密码。自行设置的初始密码不强制更换。</p>

<Heading id="channel">3. 建渠道</Heading>

<p>渠道就是一个上游服务。在「渠道」页点「新建渠道」，填：</p>

<ul>
	<li><strong>名字</strong>：随意，日志和统计里显示它。</li>
	<li><strong>API 格式</strong>：上游说哪种格式，这里选 OpenAI Chat。</li>
	<li>
		<strong>供应商</strong>：按谁的价目表算钱。从列表里选（例如 openai）；中转或自建服务不在列表里就勾「不在列表里」，以后在「单价」页手填单价。
	</li>
	<li>
		<strong>上游地址</strong>：例如 <code>https://api.openai.com</code>。末尾写不写 <code>/v1</code> 都行，网关会拆出来，不会拼成
		<code>/v1/v1</code>。
	</li>
	<li><strong>上游模型</strong>：可以先空着，下一步同步。</li>
</ul>

<p>
	点「创建渠道」。创建后页面下方出现「渠道密钥」一节，把上游的密钥填进去，点「添加渠道密钥」。然后点「立即同步」：网关用这把密钥去问上游有哪些模型，顺便验证地址、密钥和代理都通。
</p>

<Heading id="model">4. 下发模型</Heading>

<p>
	渠道里的模型叫上游模型，客户端看不到。要让客户端能用，得把它作为下发模型发出去。在「下发模型」页点「新建下发模型」：
</p>

<ul>
	<li><strong>模型名</strong>：客户端请求里写的 <code>model</code>，例如 <code>gpt-4o</code>。</li>
	<li><strong>类型</strong>：对话。</li>
	<li>
		<strong>匹配规则</strong>：表单里已经有一条「规则 1」，匹配方式保持「精确」，模式写 <code>gpt-4o</code>。所有渠道里叫这个名字的上游模型都会成为它的候选。
	</li>
</ul>

<p>
	点「创建」，下方的「候选预览」列出会用到哪些渠道。确认没问题后勾上「下发（客户端能看到并使用）」，再点「保存」。
</p>

<Heading id="api-key">5. 建 API 密钥</Heading>

<p>
	在「API 密钥」页填个名字创建，可以设置到期时间，也可以不设。完整的密钥只显示这一次，先复制下来；库里只存它的指纹，以后看不到原文。
</p>

<Heading id="first-request">6. 发第一个请求</Heading>

<Code code={chat} label="sh" />

<p>看看客户端能用哪些模型：</p>

<Code code={models} label="sh" />

<p>
	回到管理页面的「请求日志」，能看到这次请求落在哪个渠道、用了多少 token、花了多少钱。点进去能看每次尝试的细节。
</p>

<Heading id="next">接下来</Heading>

<ul>
	<li>把常用客户端接上来：<a href={resolve('/clients')}>客户端接入</a>。</li>
	<li>多接几个渠道做备份和分流：<a href={resolve('/concepts')}>概念</a>里讲了优先级、权重和冷却。</li>
	<li>正式部署前读一遍<a href={resolve('/deploy')}>部署与运维</a>，尤其是备份那一节。</li>
</ul>
