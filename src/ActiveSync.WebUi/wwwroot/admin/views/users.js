// User management: the merged (config ⊕ database) view with provenance; DB-backed entries
// are editable, config-only entries become editable once saved (the row then shadows
// config, exactly like `eas user add`). Passwords are write-only: empty = keep the stored
// value, the explicit clear button removes it.

import { api } from '/shared/api.js';
import { h, render as renderInto, table, toast, confirmDialog } from '/shared/ui.js';
import { schemaForm, schemaKeys, listRoot } from '/shared/schema-form.js';

const ROLES = ['MailStore', 'MailSubmit', 'Calendar', 'Tasks', 'Contacts', 'Notes', 'Oof'];

export async function render(container) {
	// The backend schemas turn each role's override into a real form: pick a provider, fill
	// the fields it says it needs. Nobody has to know that CardDAV calls its server "BaseUrl".
	const [users, providers, globalRoles] = await Promise.all([
		api('/admin/api/users'),
		api('/admin/api/backends/providers'),
		api('/admin/api/backends'),
	]);

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

	// Deep link: #/users/<login> (e.g. from the dashboard or devices page) opens that user's
	// editor — or, when the login isn't a declared account yet (a pass-through user seen only in
	// device/session state), a pre-filled "Add user" form so the link still lands somewhere.
	const target = decodeURIComponent(location.hash.replace(/^#\/users\/?/, ''));
	if (target) {
		const linked = users.find(u => u.login.toLowerCase() === target.toLowerCase());
		openEditor(linked ?? null, linked ? null : target);
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

	function openEditor(user, prefillLogin = null) {
		const isNew = user === null;
		const login = h('input', {
			value: user?.login ?? prefillLogin ?? '', placeholder: 'gateway login (what the phone authenticates as)',
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

		const roleEditors = ROLES.map(role => roleEditor(
			role, user?.backends?.[role], providers, globalRoles.find(g => g.role === role)));

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

/**
 * One role's per-user override. Credentials are host-reserved fields (they are not provider
 * settings); everything below them is rendered from the schema of whichever provider ends up
 * serving the role — the one selected here, or the global one when inheriting.
 */
function roleEditor(role, current, providers, globalRole) {
	const candidates = providers.filter(p => p.roles.includes(role));
	const inheritedProvider = globalRole?.provider ?? null;

	const enabled = h('select', {},
		h('option', { value: '', selected: current?.enabled == null }, '(default: on)'),
		h('option', { value: 'true', selected: current?.enabled === true }, 'on'),
		h('option', { value: 'false', selected: current?.enabled === false }, 'off'));
	const provider = h('select', {},
		h('option', { value: '', selected: !current?.provider },
			inheritedProvider ? `(inherit: ${inheritedProvider})` : '(inherit: none configured)'),
		candidates.map(p => h('option', { value: p.name, selected: p.name === current?.provider }, p.name)));
	const userName = h('input', {
		value: current?.userName ?? '', placeholder: 'inherits the MailStore user (gateway login)', spellcheck: 'false',
	});
	const password = h('input', {
		type: 'password', autocomplete: 'new-password',
		placeholder: current?.passwordSet ? '••• set — leave empty to keep' : 'inherits the login password',
	});
	let clearPassword = false;

	const settingsBox = h('div', {});
	const inheritNote = h('div', { class: 'field-help' });
	let form = null;
	let advanced = null;

	function drawSettings() {
		const effective = provider.value || inheritedProvider;
		const described = providers.find(p => p.name === effective);
		const fields = described?.schemas?.[role] ?? [];

		// Settings inherit the global section ONLY while the provider matches it — a switched
		// provider's keys mean something else entirely (the same rule AccountResolver applies).
		const inheritsGlobal = !provider.value || provider.value === inheritedProvider;
		const inherited = {};
		if (inheritsGlobal)
			for (const setting of globalRole?.settings ?? []) inherited[setting.key] = setting.value;

		inheritNote.replaceChildren(inheritsGlobal
			? 'Empty fields inherit the global setting shown dimmed.'
			: `Different provider than the global ${inheritedProvider ?? 'none'} — these settings start fresh.`);

		const values = current?.settings ?? {};
		const known = schemaKeys(fields);
		const leftover = Object.entries(values).filter(([k]) => !known.has(listRoot(k)));

		form = schemaForm({ fields, values, inherited });
		advanced = rawEditor(leftover, fields.length > 0);
		settingsBox.replaceChildren(form.node, advanced.node);
	}

	provider.addEventListener('change', drawSettings);
	drawSettings();

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
			inheritNote,
			settingsBox));

	return {
		role,
		element,
		value() {
			// The PUT replaces the override wholesale, so a value equal to what is inherited is
			// simply left out — and keys the schema does not cover ride along from the raw rows.
			const collected = { ...form.collect(), ...advanced.collect() };
			const settings = {};
			for (const [key, value] of Object.entries(collected))
				if (value !== null && value !== undefined) settings[key] = value;

			const result = {
				enabled: enabled.value === '' ? null : enabled.value === 'true',
				provider: provider.value || null,
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

/** Key/value rows for settings the effective provider does not describe, so they survive a save. */
function rawEditor(existing, collapsed) {
	const rows = existing.map(([key, value]) => [key, value]);
	const list = h('div', {});

	function draw() {
		renderInto(list, rows.map((pair, index) =>
			h('div', { class: 'list-row' },
				h('input', { value: pair[0], placeholder: 'Key', spellcheck: 'false',
					oninput: e => { rows[index][0] = e.target.value; } }),
				h('input', { value: pair[1] ?? '', placeholder: 'Value', spellcheck: 'false',
					oninput: e => { rows[index][1] = e.target.value; } }),
				h('button', { class: 'icon', type: 'button', title: 'Remove',
					onclick: () => { rows.splice(index, 1); draw(); } }, '✕'))));
		list.append(h('button', { type: 'button',
			onclick: () => { rows.push(['', '']); draw(); } }, 'Add setting'));
	}

	draw();

	return {
		node: h('details', { open: !collapsed || existing.length > 0 },
			h('summary', {}, 'Advanced — settings this provider does not describe'),
			list),
		collect() {
			const settings = {};
			for (const [key, value] of rows)
				if (key.trim()) settings[key.trim()] = value;
			return settings;
		},
	};
}

function refresh(container) {
	import('/admin/views/users.js').then(m => m.render(container));
}
