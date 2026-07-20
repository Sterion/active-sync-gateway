// User management: the merged (config ⊕ database) view with provenance; DB-backed entries
// are editable, config-only entries become editable once saved (the row then shadows
// config, exactly like `eas user add`). Passwords are write-only: empty = keep the stored
// value, the explicit clear button removes it.

import { api } from '/shared/api.js';
import { h, render as renderInto, table, toast, confirmDialog } from '/shared/ui.js';

const ROLES = ['MailStore', 'MailSubmit', 'Calendar', 'Tasks', 'Contacts', 'Notes', 'Oof'];

export async function render(container) {
	const users = await api('/admin/api/users');

	const list = h('div', {});
	const editor = h('div', {});

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Users'),
		h('div', { class: 'card' },
			h('div', { style: 'display:flex; justify-content:space-between; align-items:center; margin-bottom:10px' },
				h('h2', { style: 'margin:0' }, 'Declared accounts'),
				h('button', { class: 'primary', onclick: () => openEditor(null) }, 'Add user')),
			list),
		editor);

	renderList();

	// Deep link: #/users/<login> (e.g. from the dashboard) opens that user's editor.
	const target = decodeURIComponent(location.hash.replace(/^#\/users\/?/, ''));
	if (target) {
		const linked = users.find(u => u.login.toLowerCase() === target.toLowerCase());
		if (linked) openEditor(linked);
	}

	function renderList() {
		renderInto(list, users.length === 0
			? h('div', { class: 'notice' }, 'No declared users — every login is pure pass-through.')
			: table([
				{ label: 'Login', cell: u => h('a', { href: '#', onclick: e => { e.preventDefault(); openEditor(u); } }, u.login) },
				{ label: 'Origin', cell: u => h('span', { class: u.origin === 'config' ? 'badge' : 'badge accent' }, u.origin) },
				{ label: 'Mail', cell: u => u.mailAddress ?? '—' },
				{ label: 'Password', cell: u => u.passwordSet ? `set (${u.passwordFormat})` : '—' },
				{ label: 'Admin', cell: u => u.admin ? h('span', { class: 'badge ok' }, 'admin') : '—' },
				{ label: 'Overrides', cell: u => u.backends ? Object.keys(u.backends).join(', ') : '—' },
			], users));
	}

	function openEditor(user) {
		const isNew = user === null;
		const login = h('input', {
			value: user?.login ?? '', placeholder: 'gateway login (what the phone authenticates as)',
			spellcheck: 'false', ...(isNew ? {} : { disabled: true }),
		});
		const mail = h('input', {
			value: user?.mailAddress ?? '',
			placeholder: 'defaults to the login when it contains @', spellcheck: 'false',
		});
		const admin = h('input', { type: 'checkbox', style: 'width:auto', ...(user?.admin ? { checked: true } : {}) });
		const password = h('input', {
			type: 'password', autocomplete: 'new-password',
			placeholder: user?.passwordSet ? '••• set — leave empty to keep' : 'unset — uses the mail password',
		});
		let clearPassword = false;

		const roleEditors = ROLES.map(role => roleEditor(role, user?.backends?.[role]));

		renderInto(editor, h('div', { class: 'card' },
			h('h2', {}, isNew ? 'Add user' : `Edit ${user.login}`),
			user?.origin === 'config'
				? h('div', { class: 'notice' },
					'This entry comes from configuration — saving stores a database copy that replaces it (delete the copy to fall back).')
				: null,
			h('label', {}, 'Login'), login,
			h('label', {}, 'Mail address',
				(user?.mailAddress ?? '') === '' ? h('span', { class: 'badge default', style: 'margin-left:8px' }, 'default') : null),
			mail,
			h('label', {}, 'Gateway password (decouples the phone password from the mail backend)'),
			h('div', { style: 'display:flex; gap:8px' }, password,
				h('button', { onclick: () => {
					clearPassword = true;
					password.value = '';
					password.placeholder = 'will be REMOVED on save';
				} }, 'Clear')),
			h('label', { style: 'display:flex; align-items:center; gap:8px; margin-top:12px' },
				admin, 'Web admin access (/admin)'),
			h('h2', { style: 'margin-top:18px' }, 'Backend role overrides'),
			...roleEditors.map(r => r.element),
			h('div', { style: 'display:flex; gap:8px; margin-top:14px' },
				h('button', { class: 'primary', onclick: save }, isNew ? 'Create' : 'Save'),
				user?.origin?.startsWith('db')
					? h('button', { class: 'danger', onclick: remove }, 'Delete database entry')
					: null,
				h('button', { onclick: () => renderInto(editor) }, 'Close'))));
		editor.scrollIntoView({ behavior: 'smooth' });

		async function save() {
			const name = login.value.trim();
			if (!name) { toast('A login is required.', 'error'); return; }
			const backends = {};
			for (const r of roleEditors) {
				const value = r.value();
				if (value) backends[r.role] = value;
			}
			try {
				await api(`/admin/api/users/${encodeURIComponent(name)}`, {
					method: 'PUT',
					body: {
						mailAddress: mail.value.trim() || null,
						admin: admin.checked,
						password: clearPassword ? '' : (password.value || null),
						backends: Object.keys(backends).length ? backends : null,
					},
				});
				toast(`Saved ${name}. A running gateway applies this within ~1 s.`, 'ok');
				refresh(container);
			} catch (e) {
				toast(e.body?.error ?? 'Saving failed.', 'error');
			}
		}

		async function remove() {
			if (!await confirmDialog(`Delete the database entry for ${user.login}?`, 'Delete')) return;
			try {
				const result = await api(`/admin/api/users/${encodeURIComponent(user.login)}`, { method: 'DELETE' });
				toast(result.configFallback
					? `Removed — the config entry for ${user.login} is active again.`
					: `Removed ${user.login}.`, 'ok');
				refresh(container);
			} catch (e) {
				toast(e.body?.error ?? 'Delete failed.', 'error');
			}
		}
	}
}

function roleEditor(role, current) {
	const enabled = h('select', {},
		h('option', { value: '', selected: current?.enabled == null }, '(default: on)'),
		h('option', { value: 'true', selected: current?.enabled === true }, 'on'),
		h('option', { value: 'false', selected: current?.enabled === false }, 'off'));
	const provider = h('input', {
		value: current?.provider ?? '', placeholder: 'inherits the global provider', spellcheck: 'false',
	});
	const userName = h('input', {
		value: current?.userName ?? '', placeholder: 'inherits the MailStore user (gateway login)', spellcheck: 'false',
	});
	const password = h('input', {
		type: 'password', autocomplete: 'new-password',
		placeholder: current?.passwordSet ? '••• set — leave empty to keep' : 'inherits the login password',
	});
	let clearPassword = false;
	const settingsRows = h('div', {});
	const rows = Object.entries(current?.settings ?? {});
	renderSettings();

	const element = h('details', current ? { open: true } : {},
		h('summary', { style: 'cursor:pointer; padding:6px 0' }, role,
			current ? h('span', { class: 'badge accent', style: 'margin-left:8px' }, 'overridden') : null),
		h('div', { style: 'padding:4px 0 10px 14px' },
			h('label', {}, 'Enabled'), enabled,
			h('label', {}, 'Provider'), provider,
			h('label', {}, 'Backend user'), userName,
			h('label', {}, 'Backend password'),
			h('div', { style: 'display:flex; gap:8px' }, password,
				h('button', { onclick: () => {
					clearPassword = true;
					password.value = '';
					password.placeholder = 'will be REMOVED on save';
				} }, 'Clear')),
			h('label', {}, 'Settings (provider-specific keys, e.g. Host, Port, BaseUrl — the token-auth keys land here later)'),
			settingsRows,
			h('button', { onclick: () => { rows.push(['', '']); renderSettings(); } }, 'Add setting')));

	function renderSettings() {
		renderInto(settingsRows, rows.map((pair, index) =>
			h('div', { style: 'display:flex; gap:8px; margin-bottom:6px' },
				h('input', { value: pair[0], placeholder: 'Key', spellcheck: 'false',
					oninput: e => { rows[index][0] = e.target.value; } }),
				h('input', { value: pair[1] ?? '', placeholder: 'Value', spellcheck: 'false',
					oninput: e => { rows[index][1] = e.target.value; } }),
				h('button', { onclick: () => { rows.splice(index, 1); renderSettings(); } }, '✕'))));
	}

	return {
		role,
		element,
		value() {
			const settings = {};
			for (const [key, value] of rows)
				if (key.trim()) settings[key.trim()] = value;
			const result = {
				enabled: enabled.value === '' ? null : enabled.value === 'true',
				provider: provider.value.trim() || null,
				userName: userName.value.trim() || null,
				password: clearPassword ? '' : (password.value || null),
				settings: Object.keys(settings).length ? settings : null,
			};
			const empty = result.enabled === null && !result.provider && !result.userName &&
				result.password === null && !result.settings;
			// Keep the override when only a stored password exists (password: null = keep it).
			return empty && !current?.passwordSet ? null : result;
		},
	};
}

function refresh(container) {
	import('/admin/views/users.js').then(m => m.render(container));
}
