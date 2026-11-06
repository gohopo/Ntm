import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// 文档站：全部预渲染成静态页，单独发布，不随 Ntm.Server 打包（Q9）。
// 发布到 GitHub Pages 时站点在 /Ntm 下，由工作流传 BASE_PATH=/Ntm（Q324）；本地构建和预览不设。
// 没装 @types/node，所以这样取环境变量。
const env = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};
const base = (env.BASE_PATH ?? '') as '' | `/${string}`;

export default defineConfig({
	plugins: [
		sveltekit({
			compilerOptions: {
				runes: ({ filename }) =>
					filename.split(/[/\\]/).includes('node_modules') ? undefined : true
			},
			adapter: adapter(),
			paths: { base }
		})
	]
});
