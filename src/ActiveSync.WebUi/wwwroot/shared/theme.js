// Theme switch: dark is the design default; the OS preference applies until the user pins a
// theme, which is stored in localStorage and stamped as [data-theme] on <html>.

const KEY = 'eas-webui-theme';

export function initTheme() {
	const saved = localStorage.getItem(KEY);
	if (saved === 'light' || saved === 'dark')
		document.documentElement.dataset.theme = saved;
}

export function toggleTheme() {
	const current = document.documentElement.dataset.theme
		?? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
	const next = current === 'dark' ? 'light' : 'dark';
	document.documentElement.dataset.theme = next;
	localStorage.setItem(KEY, next);
}

export function bindThemeToggle(button) {
	button.addEventListener('click', toggleTheme);
}
