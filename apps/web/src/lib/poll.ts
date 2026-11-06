/**
 * 每隔一段时间重新取一次，页面不可见时停下来（Q104）。在 onMount 里调用，把返回值当清理函数。
 */
export function poll(load: () => void | Promise<void>, intervalMs: number): () => void {
	let timer: ReturnType<typeof setInterval> | undefined;

	function stop() {
		if (timer !== undefined) clearInterval(timer);
		timer = undefined;
	}

	function start() {
		stop();
		timer = setInterval(() => void load(), intervalMs);
	}

	function onVisibility() {
		if (document.visibilityState === 'visible') {
			void load();
			start();
		} else {
			stop();
		}
	}

	if (document.visibilityState === 'visible') start();
	document.addEventListener('visibilitychange', onVisibility);
	return () => {
		stop();
		document.removeEventListener('visibilitychange', onVisibility);
	};
}
