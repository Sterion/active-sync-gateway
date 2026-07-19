// User portal shell: login + the single account view (./views/account.js).

import { api } from '/shared/api.js';
import { initTheme, bindThemeToggle } from '/shared/theme.js';
import { h, render } from '/shared/ui.js';

initTheme();
bindThemeToggle(document.getElementById('theme-toggle'));

const loginView = document.getElementById('login');
const shell = document.getElementById('shell');

document.addEventListener('eas:unauthorized', showLogin);

document.getElementById('login-form').addEventListener('submit', async event => {
	event.preventDefault();
	const error = document.getElementById('login-error');
	error.classList.add('hidden');
	try {
		const result = await api('/user/api/login', {
			body: {
				username: document.getElementById('login-user').value,
				password: document.getElementById('login-pass').value,
			},
		});
		showShell(result);
	} catch (e) {
		error.textContent = e.status === 403
			? 'This login is blocked.'
			: e.status === 429
				? 'Too many attempts — try again later.'
				: 'Wrong login or password.';
		error.classList.remove('hidden');
	}
});

document.getElementById('logout').addEventListener('click', async () => {
	await api('/user/api/logout', { body: {} });
	showLogin();
});

async function boot() {
	try {
		const mode = await api('/user/api/auth/mode');
		document.getElementById('login-local').classList.toggle('hidden', mode.mode === 'oidc');
		document.getElementById('login-oidc').classList.toggle('hidden', mode.mode !== 'oidc');
		document.getElementById('login-sso')?.addEventListener('click', () => {
			location.href = '/user/oidc/login';
		});
	} catch { /* mode probe failing is not fatal */ }

	try {
		const session = await api('/user/api/session');
		showShell(session);
	} catch {
		showLogin();
	}
}

function showLogin() {
	shell.classList.add('hidden');
	loginView.classList.remove('hidden');
}

async function showShell(session) {
	document.getElementById('who').textContent = session.login ?? '';
	document.getElementById('nav-admin').classList.toggle('hidden', !session.admin);
	loginView.classList.add('hidden');
	shell.classList.remove('hidden');
	const container = document.getElementById('view');
	try {
		const module = await import('/user/views/account.js');
		await module.render(container);
	} catch (e) {
		if (e?.status === 401) return;
		render(container,
			h('h1', { class: 'page-title' }, 'Account'),
			h('div', { class: 'notice' }, 'This view is not available yet.'));
	}
}

boot();
