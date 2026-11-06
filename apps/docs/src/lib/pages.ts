// 文档的页面清单：侧栏、上一页和下一页都按这里的顺序（Q320、Q325）。
import type { PageRouteId } from '$app/types';

export interface DocPage {
	path: PageRouteId;
	title: string;
}

export interface DocGroup {
	title: string;
	pages: DocPage[];
}

export const groups: DocGroup[] = [
	{
		title: '开始',
		pages: [
			{ path: '/', title: '介绍' },
			{ path: '/quick-start', title: '快速开始' }
		]
	},
	{
		title: '部署',
		pages: [
			{ path: '/deploy', title: '部署与运维' },
			{ path: '/configuration', title: '配置参考' }
		]
	},
	{
		title: '使用',
		pages: [
			{ path: '/concepts', title: '概念' },
			{ path: '/clients', title: '客户端接入' },
			{ path: '/conversion', title: '协议互转' },
			{ path: '/billing', title: '计费' },
			{ path: '/observability', title: '可观测与工作台' }
		]
	},
	{
		title: '排错',
		pages: [{ path: '/troubleshooting', title: '排错' }]
	}
];

export const pages: DocPage[] = groups.flatMap((group) => group.pages);

/** 仓库地址，ADR 等链接到这里（Q327）。 */
export const repo = 'https://github.com/gohopo/Ntm';

export function adr(file: string): string {
	return `${repo}/blob/main/docs/adr/${file}`;
}
