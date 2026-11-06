<script lang="ts">
	import { resolve } from '$app/paths';
	import { Code, Heading } from '#lib';

	const openaiPython = `from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:8080/v1",
    api_key="你的 API 密钥",
)

reply = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "你好"}],
)
print(reply.choices[0].message.content)`;

	const openaiNode = `import OpenAI from 'openai';

const client = new OpenAI({
  baseURL: 'http://localhost:8080/v1',
  apiKey: '你的 API 密钥',
});`;

	const openaiEnv = `export OPENAI_BASE_URL=http://localhost:8080/v1
export OPENAI_API_KEY=你的 API 密钥`;

	const anthropicPython = `from anthropic import Anthropic

client = Anthropic(
    base_url="http://localhost:8080",
    api_key="你的 API 密钥",
)

reply = client.messages.create(
    model="claude-sonnet-4-5",
    max_tokens=1024,
    messages=[{"role": "user", "content": "你好"}],
)
print(reply.content[0].text)`;

	const claudeCode = `{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:8080",
    "ANTHROPIC_AUTH_TOKEN": "你的 API 密钥",
    "ANTHROPIC_MODEL": "claude-sonnet-4-5",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL": "claude-haiku-4-5"
  }
}`;

	const codex = `model = "gpt-5"
model_provider = "ntm"

[model_providers.ntm]
name = "南天门"
base_url = "http://localhost:8080/v1"
env_key = "NTM_API_KEY"
wire_api = "responses"`;

	const codexEnv = `export NTM_API_KEY=你的 API 密钥`;
</script>

<h1>客户端接入</h1>

<p class="lead">
	客户端只需要两样东西：网关的地址和一个 <a href={resolve('/concepts') + '#api-key'}>API 密钥</a>。下面的例子假设网关在
	<code>http://localhost:8080</code>，按你的实际地址换掉。
</p>

<Heading id="basics">通用规则</Heading>

<ul>
	<li>
		API 密钥放在 <code>Authorization: Bearer 密钥</code> 或 <code>x-api-key: 密钥</code> 里都行，两个都有时用前者。所有接口都一样。
	</li>
	<li>
		<code>model</code> 写<a href={resolve('/concepts') + '#published-model'}>下发模型</a>的名字。<code>GET /v1/models</code>
		列出这个 API 密钥能用的下发模型。请求带了 <code>anthropic-version</code> 或 <code>x-api-key</code> 头时按 Anthropic 的格式返回，而且只列对话模型；否则按 OpenAI 的格式返回。
	</li>
	<li>
		<strong>地址带不带 <code>/v1</code></strong>：OpenAI 的 SDK 和 Codex 要带（<code>http://localhost:8080/v1</code>），Anthropic 的
		SDK 和 Claude Code 不带（<code>http://localhost:8080</code>），它们自己会拼 <code>/v1/messages</code>。其他客户端按它要的是哪一种来填。
	</li>
	<li>
		客户端用哪种格式，和渠道是哪种格式无关：用 OpenAI SDK 也能用 Anthropic 格式的渠道，网关会转换。转换的细节和限制见<a
			href={resolve('/conversion')}>协议互转</a
		>。
	</li>
</ul>

<Heading id="openai">OpenAI SDK</Heading>

<p>Python：</p>

<Code code={openaiPython} label="Python" />

<p>Node.js：</p>

<Code code={openaiNode} label="TypeScript" />

<p>两个 SDK 都认环境变量，不想改代码时可以这样：</p>

<Code code={openaiEnv} label="sh" />

<p>
	嵌入用 <code>client.embeddings.create</code>，生图用 <code>client.images.generate</code>，<code>model</code> 写对应类型的下发模型；Responses
	用 <code>client.responses.create</code>。
</p>

<Heading id="anthropic">Anthropic SDK</Heading>

<Code code={anthropicPython} label="Python" />

<p>
	TypeScript 的 SDK 是 <code>baseURL</code> 和 <code>apiKey</code>。也可以设环境变量 <code>ANTHROPIC_BASE_URL</code> 和
	<code>ANTHROPIC_API_KEY</code>。注意地址不带 <code>/v1</code>。
</p>

<Heading id="claude-code">Claude Code</Heading>

<p>
	把下面的 <code>env</code> 写进 <code>~/.claude/settings.json</code>（对所有项目生效），或项目里的
	<code>.claude/settings.local.json</code>（只对这个项目生效，不会提交）。不要写进要提交的 <code>.claude/settings.json</code>，那样密钥会进仓库。
</p>

<Code code={claudeCode} label="settings.json" />

<ul>
	<li>
		<code>ANTHROPIC_AUTH_TOKEN</code> 把密钥放在 <code>Authorization: Bearer</code> 里。也可以改用
		<code>ANTHROPIC_API_KEY</code>（放在 <code>x-api-key</code> 里），交互模式下第一次用时 Claude Code 会让你确认。
	</li>
	<li>
		<code>ANTHROPIC_MODEL</code> 是主模型，<code>ANTHROPIC_DEFAULT_HAIKU_MODEL</code> 是做后台小任务的模型，都写下发模型的名字。还有
		<code>ANTHROPIC_DEFAULT_OPUS_MODEL</code>、<code>ANTHROPIC_DEFAULT_SONNET_MODEL</code>，按需设。
	</li>
	<li>在 Claude Code 里输入 <code>/status</code> 能看到实际用的地址和模型。</li>
	<li>
		Claude Code 会调用 <code>/v1/messages/count_tokens</code> 估算上下文长度，网关支持这个接口；渠道不支持时网关在本地估算。
	</li>
	<li>
		渠道不是 Anthropic 官方、回 400「Extra inputs are not permitted」时，加 <code>CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS=1</code>，让
		Claude Code 不发实验性的参数。
	</li>
</ul>

<p class="note">
	Claude Code 的设置项会随版本变化，以<a href="https://code.claude.com/docs/en/llm-gateway">官方的 LLM 网关文档</a>为准。
</p>

<Heading id="codex">Codex</Heading>

<p>
	Codex 只用 Responses 格式（<code>/v1/responses</code>）。在 <code>~/.codex/config.toml</code> 里加一个提供方：
</p>

<Code code={codex} label="config.toml" />

<p>密钥放在 <code>env_key</code> 指定的环境变量里：</p>

<Code code={codexEnv} label="sh" />

<ul>
	<li><code>base_url</code> 带 <code>/v1</code>，Codex 自己拼 <code>/responses</code>。</li>
	<li><code>wire_api</code> 只能是 <code>"responses"</code>，新版 Codex 已经不支持 <code>"chat"</code>。</li>
	<li>提供方只能写在用户目录的 <code>~/.codex/config.toml</code> 里，项目里的 <code>.codex/config.toml</code> 不读这些设置。</li>
	<li>
		渠道可以是任意格式，网关会把 Responses 转过去；但 <code>previous_response_id</code>、后台模式这些依赖上游状态的功能只有 Responses
		格式的渠道能做，见<a href={resolve('/conversion') + '#responses-state'}>Responses 的上游状态</a>。
	</li>
</ul>

<p class="note">
	Codex 的配置项以<a href="https://developers.openai.com/codex/config-advanced">官方文档</a>为准。
</p>

<Heading id="others">其他客户端</Heading>

<p>
	说「兼容 OpenAI」的客户端一般填 <code>http://localhost:8080/v1</code>；说「兼容 Anthropic」的一般填
	<code>http://localhost:8080</code>。有的客户端会自己补 <code>/v1</code>，路径拼错时网关回 404，请求日志里不会有记录，这时换另一种写法试试。
</p>
