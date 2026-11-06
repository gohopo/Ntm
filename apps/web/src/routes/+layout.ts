import { redirect } from '@sveltejs/kit';
import { resolve } from '$app/paths';
import type { LayoutLoad } from './$types';

// 管理面板只在浏览器里跑：没有 SSR，未知路径由 200.html 接住后交给客户端路由。
export const ssr = false;

/** 每次导航都问一下会话还在不在；不在就去登录页，登完回到原来的地方。 */
export const load: LayoutLoad = async ({ fetch, url }) => {
	const onLogin = url.pathname === resolve('/login');
	const response = await fetch('/api/auth/me', { credentials: 'same-origin' });
	const signedIn = response.ok;

	if (!signedIn && !onLogin) {
		redirect(307, resolve('/login') + '?next=' + encodeURIComponent(url.pathname + url.search));
	}
	if (signedIn && onLogin) {
		redirect(307, resolve('/'));
	}
	return { signedIn };
};
