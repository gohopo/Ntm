import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// 管理面板：纯 SPA，由 Ntm.Server 托管（Q9）。
// fallback 用 200.html 而不是 index.html，避免和预渲染出的首页冲突；
// 不预压缩，压缩交给 ASP.NET Core 的 MapStaticAssets。
// 直接输出到后端的 wwwroot（整个目录是生成物，已 gitignore，每次构建会先清空）；
// MapStaticAssets 的清单在 dotnet build 时生成，所以要先 pnpm build:web 再构建 Ntm.Server。
// 路径相对 apps/web（pnpm 在包目录里跑脚本）。
const wwwroot = '../../src/Ntm.Server/wwwroot';

export default defineConfig({
	plugins: [
		sveltekit({
			compilerOptions: {
				runes: ({ filename }) =>
					filename.split(/[/\\]/).includes('node_modules') ? undefined : true
			},
			adapter: adapter({ pages: wwwroot, assets: wwwroot, fallback: '200.html', precompress: false })
		})
	],
	server: {
		// 开发时把 API 和网关端点转给本地的 Ntm.Server。
		proxy: {
			'/api': 'http://127.0.0.1:5278',
			'/v1': 'http://127.0.0.1:5278'
		}
	}
});
