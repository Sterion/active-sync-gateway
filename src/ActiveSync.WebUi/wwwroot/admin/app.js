// Admin SPA shell: login, hash router, view host. Views live in ./views/ — one ES module per
// view exporting `render(container)`.

import { api } from '/shared/api.js';
import { initTheme, bindThemeToggle } from '/shared/theme.js';
import { h, render } from '/shared/ui.js';

const VIEWS = ['dashboard', 'settings', 'users', 'devices', 'shares', 'logs', 'state'];

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
		const result = await api('/admin/api/login', {
			body: {
				username: document.getElementById('login-user').value,
				password: document.getElementById('login-pass').value,
			},
		});
		showShell(result);
	} catch (e) {
		error.textContent = e.status === 403
			? 'This account has no admin access.'
			: e.status === 429
				? 'Too many attempts — try again later.'
				: 'Wrong login or password.';
		error.classList.remove('hidden');
	}
});

document.getElementById('logout').addEventListener('click', async () => {
	await api('/admin/api/logout', { body: {} });
	showLogin();
});

window.addEventListener('hashchange', route);

async function boot() {
	// SSO round-trips land back here with a hash flag instead of an error page.
	if (location.hash === '#sso-denied' || location.hash === '#sso-failed') {
		const error = document.getElementById('login-error');
		error.textContent = location.hash === '#sso-denied'
			? 'Signed in at the identity provider, but this gateway has no matching account (or it is blocked).'
			: 'SSO sign-in failed or was cancelled.';
		error.classList.remove('hidden');
		history.replaceState(null, '', location.pathname);
	}

	try {
		const mode = await api('/admin/api/auth/mode');
		document.getElementById('login-local').classList.toggle('hidden', mode.mode === 'oidc');
		document.getElementById('login-oidc').classList.toggle('hidden', mode.mode !== 'oidc');
		document.getElementById('login-sso')?.addEventListener('click', () => {
			location.href = '/admin/oidc/login';
		});
	} catch { /* mode probe failing is not fatal */ }

	try {
		const session = await api('/admin/api/session');
		showShell(session);
	} catch {
		showLogin();
	}
}

function showLogin() {
	shell.classList.add('hidden');
	loginView.classList.remove('hidden');
}

function showShell(session) {
	document.getElementById('who').textContent = session.login ?? '';
	loginView.classList.add('hidden');
	shell.classList.remove('hidden');
	if (!location.hash) location.hash = '#/dashboard';
	route();
}

async function route() {
	if (shell.classList.contains('hidden')) return;
	const name = (location.hash.replace(/^#\//, '') || 'dashboard').split('/')[0];
	const view = VIEWS.includes(name) ? name : 'dashboard';
	for (const item of document.querySelectorAll('.nav-item[data-view]'))
		item.classList.toggle('active', item.dataset.view === view);
	const container = document.getElementById('view');
	try {
		const module = await import(`/admin/views/${view}.js`);
		await module.render(container);
	} catch (e) {
		if (e?.status === 401) return; // the unauthorized handler already switched views
		render(container,
			h('h1', { class: 'page-title' }, view[0].toUpperCase() + view.slice(1)),
			h('div', { class: 'notice' }, 'This view is not available yet.'));
	}
}

boot();
