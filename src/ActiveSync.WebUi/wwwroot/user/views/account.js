// The user portal's single view: change the gateway password and manage the caller's own
// backend role credentials/settings. Provider switches and role on/off are admin-only.

import { api } from '/shared/api.js';
import { h, render as renderInto, toast } from '/shared/ui.js';

const ROLES = ['MailStore', 'MailSubmit', 'Calendar', 'Tasks', 'Contacts', 'Notes', 'Oof'];

export async function render(container) {
	const me = await api('/user/api/me');

	const current = h('input', { type: 'password', autocomplete: 'current-password' });
	const next = h('input', { type: 'password', autocomplete: 'new-password' });
	const confirm = h('input', { type: 'password', autocomplete: 'new-password' });

	renderInto(container,
		h('h1', { class: 'page-title' }, 'My account'),
		h('div', { class: 'card' },
			h('h2', {}, 'Account'),
			h('p', {}, 'Login: ', h('strong', {}, me.login),
				me.mailAddress ? h('span', { style: 'color:var(--fg-muted)' }, ` · ${me.mailAddress}`) : null,
				me.admin ? h('span', { class: 'badge accent', style: 'margin-left:8px' }, 'admin') : null)),
		h('div', { class: 'card' },
			h('h2', {}, 'Change password'),
			h('div', { class: 'notice' }, me.passwordSet
				? 'Your gateway password is what the phone and this portal authenticate with — it is separate from the mail backend password.'
				: 'You currently sign in with your MAIL password. Setting a gateway password here decouples the two (the phone then uses the new password).'),
			h('label', {}, 'Current password'), current,
			h('label', {}, 'New password'), next,
			h('label', {}, 'Repeat new password'), confirm,
			h('button', { class: 'primary', style: 'margin-top:12px', onclick: changePassword }, 'Change password')),
		h('div', { class: 'card' },
			h('h2', {}, 'Backend credentials'),
			h('div', { class: 'notice' },
				'Per-role backend login overrides. Empty fields inherit: the user name defaults to ',
				'your MailStore user (your login), the password to your sign-in password.'),
			...ROLES.map(role => roleCard(role, me.backends?.[role]))));

	async function changePassword() {
		if (next.value !== confirm.value) {
			toast('The new passwords do not match.', 'error');
			return;
		}
		try {
			await api('/user/api/password', {
				method: 'PUT', body: { current: current.value, new: next.value },
			});
			toast('Password changed — use it for the phone and this portal from now on.', 'ok');
			current.value = next.value = confirm.value = '';
		} catch (e) {
			toast(e.body?.error ?? 'Password change failed.', 'error');
		}
	}
}

function roleCard(role, existing) {
	const userName = h('input', {
		value: existing?.userName ?? '', spellcheck: 'false',
		placeholder: role === 'MailStore' ? 'defaults to your login' : 'inherits the MailStore user',
	});
	const password = h('input', {
		type: 'password', autocomplete: 'new-password',
		placeholder: existing?.passwordSet ? '••• set — leave empty to keep' : 'inherits your sign-in password',
	});
	let clearPassword = false;
	const rows = Object.entries(existing?.settings ?? {});
	const settingsRows = h('div', {});
	renderSettings();

	function renderSettings() {
		renderInto(settingsRows, rows.map((pair, index) =>
			h('div', { style: 'display:flex; gap:8px; margin-bottom:6px' },
				h('input', { value: pair[0], placeholder: 'Key', spellcheck: 'false',
					oninput: e => { rows[index][0] = e.target.value; } }),
				h('input', { value: pair[1] ?? '', placeholder: 'Value', spellcheck: 'false',
					oninput: e => { rows[index][1] = e.target.value; } }),
				h('button', { onclick: () => { rows.splice(index, 1); renderSettings(); } }, '✕'))));
	}

	async function save() {
		const settings = {};
		for (const [key, value] of rows)
			if (key.trim()) settings[key.trim()] = value;
		try {
			await api(`/user/api/backends/${role}`, {
				method: 'PUT',
				body: {
					userName: userName.value.trim() || null,
					password: clearPassword ? '' : (password.value || null),
					settings: Object.keys(settings).length ? settings : null,
				},
			});
			toast(`${role} saved. Applies when your session is next built (within minutes).`, 'ok');
		} catch (e) {
			toast(e.body?.error ?? `Saving ${role} failed.`, 'error');
		}
	}

	return h('details', existing ? { open: true } : {},
		h('summary', { style: 'cursor:pointer; padding:6px 0' }, role,
			existing ? h('span', { class: 'badge accent', style: 'margin-left:8px' }, 'customized') : null,
			existing?.provider ? h('span', { class: 'badge', style: 'margin-left:6px' }, existing.provider) : null,
			existing?.enabled === false ? h('span', { class: 'badge warn', style: 'margin-left:6px' }, 'off') : null),
		h('div', { style: 'padding:4px 0 10px 14px' },
			h('label', {}, 'Backend user'), userName,
			h('label', {}, 'Backend password'),
			h('div', { style: 'display:flex; gap:8px' }, password,
				h('button', { onclick: () => {
					clearPassword = true;
					password.value = '';
					password.placeholder = 'will be REMOVED on save';
				} }, 'Clear')),
			h('label', {}, 'Settings (advanced, provider-specific keys)'),
			settingsRows,
			h('div', { style: 'display:flex; gap:8px; margin-top:8px' },
				h('button', { onclick: () => { rows.push(['', '']); renderSettings(); } }, 'Add setting'),
				h('button', { class: 'primary', onclick: save }, `Save ${role}`))));
}
