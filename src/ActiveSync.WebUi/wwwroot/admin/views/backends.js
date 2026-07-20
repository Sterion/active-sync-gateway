// Backends editor: which provider fills each role, and its settings. Stored as database
// overrides over the config file — a save applies to running gateways within ~1 s.
//
// The form comes from the provider's own schema (see /shared/schema-form.js), so nothing here
// knows what an IMAP or CardDAV setting is called; a plugin's fields render the day it ships.
// Settings a provider does not describe stay editable as raw key/value rows.

import { api } from '/shared/api.js';
import { h, render as renderInto, toast, confirmDialog } from '/shared/ui.js';
import { schemaForm, schemaKeys, listRoot, sourceBadge, SECRET_MASK } from '/shared/schema-form.js';

const MAIL_ROLES = ['MailStore', 'MailSubmit'];

// What an unassigned role actually falls back to: mail has no fallback (that is the 503),
// out-of-office simply turns off, and the content roles are served by the gateway's own store.
function unassignedLabel(role) {
	if (MAIL_ROLES.includes(role)) return '(not configured)';
	return role === 'Oof' ? '(off)' : '(default: local store)';
}

function unassignedHelp(role) {
	if (MAIL_ROLES.includes(role)) return 'Pick a provider to configure this role.';
	return role === 'Oof'
		? 'Not configured — out-of-office replies are unavailable to clients.'
		: 'Not configured — this role is served by the gateway’s own store.';
}

export async function render(container) {
	const [providers, roles] = await Promise.all([
		api('/admin/api/backends/providers'),
		api('/admin/api/backends'),
	]);

	const unconfigured = MAIL_ROLES.filter(r => !roles.find(x => x.role === r)?.assigned);

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Backends'),
		h('div', { class: 'notice' },
			'Each role is served by one provider. Changes apply to running gateways within ~1 s. ',
			'Empty fields use the dimmed value below them — the config file, or the provider default.'),
		unconfigured.length
			? h('div', { class: 'notice warn' },
				`No mail backend yet (${unconfigured.join(' and ')} unassigned). `,
				'ActiveSync and Autodiscover answer 503 until you pick one here.')
			: null,
		roles.map(role => roleCard(role, providers, () => render(container))));
}

function roleCard(role, providers, reload) {
	const candidates = providers.filter(p => p.roles.includes(role.role));
	const providerSelect = h('select', {},
		h('option', { value: '', selected: !role.provider }, unassignedLabel(role.role)),
		candidates.map(p => h('option', { value: p.name, selected: p.name === role.provider }, p.name)));

	const body = h('div', {});
	const failureBox = h('div', { class: 'field-error' });
	const probeNote = h('div', { class: 'field-help' });
	let form = null;
	let advanced = null;

	function drawBody() {
		const provider = providers.find(p => p.name === providerSelect.value);
		// The probe is a plain connection attempt with no credentials — say so where it is
		// offered, and offer it only where the provider actually implements one.
		testButton.hidden = !provider?.probe;
		probeNote.replaceChildren(provider?.probe
			? 'Test connection only checks that the server answers — with pass-through ' +
			  'authentication there are no stored credentials to sign in with.'
			: '');

		if (!provider) {
			form = null;
			advanced = null;
			body.replaceChildren(h('div', { class: 'field-help' }, unassignedHelp(role.role)));
			return;
		}

		const fields = provider.schemas[role.role] ?? [];
		// Settings only carry over to a DIFFERENT provider by accident; when the provider is
		// switched the form starts from that provider's own defaults.
		const keep = provider.name === role.provider;
		const values = {};
		const sources = {};
		for (const setting of role.settings) {
			if (!keep) continue;
			values[setting.key] = setting.value;
			sources[setting.key] = setting.source;
		}

		const known = schemaKeys(fields);
		const leftover = Object.keys(values).filter(k => !known.has(listRoot(k)));

		form = schemaForm({ fields, values, sources });
		advanced = rawEditor(leftover.map(k => [k, values[k]]), fields.length > 0);
		body.replaceChildren(form.node, advanced.node);
	}

	function collect() {
		const settings = { ...(form?.collect() ?? {}), ...(advanced?.collect() ?? {}) };
		return { provider: providerSelect.value || null, settings };
	}

	async function post(path, body) {
		failureBox.replaceChildren();
		try {
			return await api(path, { method: 'POST', body });
		} catch (e) {
			failureBox.replaceChildren(e.body?.error ?? 'Request failed.');
			return null;
		}
	}

	const testButton = h('button', {
		onclick: async () => {
			const result = await post(`/admin/api/backends/${role.role}/test`, collect());
			if (!result) return;
			if (!result.supported) toast(result.detail, 'info');
			else toast(result.detail, result.reachable ? 'ok' : 'error');
		},
	}, 'Test connection');

	providerSelect.addEventListener('change', drawBody);
	drawBody();

	return h('div', { class: 'card' },
		h('div', { class: 'card-head' },
			h('h2', {}, role.role),
			sourceBadge(role.providerSource),
			h('div', { style: 'flex:1' }),
			providerSelect),
		body,
		failureBox,
		probeNote,
		h('div', { class: 'row-actions' },
			h('button', {
				class: 'primary',
				onclick: async () => {
					failureBox.replaceChildren();
					try {
						await api(`/admin/api/backends/${role.role}`, { method: 'PUT', body: collect() });
						toast(`${role.role} saved (applies live).`, 'ok');
						await reload();
					} catch (e) {
						const failures = e.body?.failures ?? [];
						const unattached = form?.markFailures(failures) ?? failures.map(f => f.message);
						failureBox.replaceChildren(unattached.join(' ') || e.body?.error || 'Saving failed.');
						toast(e.body?.error ?? 'Saving failed.', 'error');
					}
				},
			}, 'Save'),
			h('button', {
				onclick: async () => {
					const result = await post(`/admin/api/backends/${role.role}/validate`, collect());
					if (!result) return;
					const unattached = form?.markFailures(result.failures) ?? [];
					if (result.failures.length === 0) toast(`${role.role} looks valid.`, 'ok');
					else toast(unattached[0] ?? `${result.failures.length} problem(s) found.`, 'error');
				},
			}, 'Validate'),
			testButton,
			h('div', { style: 'flex:1' }),
			h('button', {
				class: 'danger',
				onclick: async () => {
					if (!await confirmDialog(
						`Discard the stored settings for ${role.role} and fall back to the config file?`,
						'Reset')) return;
					await api(`/admin/api/backends/${role.role}`, { method: 'DELETE' });
					toast(`${role.role} reset to the config file.`, 'ok');
					await reload();
				},
			}, 'Reset to config')));
}

/**
 * Raw key/value rows for settings the provider does not describe (plugin keys, or anything
 * left from an older version). They are shown rather than hidden: a full save replaces the
 * role's settings, and a key nobody can see is a key that quietly disappears.
 */
function rawEditor(existing, collapsed) {
	const rows = existing.map(([key, value]) => [key, value]);
	const list = h('div', {});

	function draw() {
		list.replaceChildren(...rows.map((row, index) => {
			const keyInput = h('input', { type: 'text', value: row[0], placeholder: 'Key', spellcheck: 'false' });
			const valueInput = h('input', {
				type: 'text', value: row[1] ?? '', placeholder: 'Value', spellcheck: 'false',
			});
			keyInput.addEventListener('input', () => { row[0] = keyInput.value; });
			valueInput.addEventListener('input', () => { row[1] = valueInput.value; });
			return h('div', { class: 'list-row' }, keyInput, valueInput,
				h('button', {
					class: 'icon', title: 'Remove', type: 'button',
					onclick: () => { rows.splice(index, 1); draw(); },
				}, '✕'));
		}), h('button', {
			type: 'button',
			onclick: () => { rows.push(['', '']); draw(); },
		}, 'Add setting'));
	}

	draw();

	const details = h('details', { open: !collapsed || existing.length > 0 },
		h('summary', {}, 'Advanced — settings this provider does not describe'),
		list);

	return {
		node: details,
		collect() {
			const changes = {};
			// Removing a row clears the stored key rather than leaving it orphaned.
			for (const [key] of existing) changes[key] = null;
			for (const [key, value] of rows)
				if (key.trim() !== '') changes[key.trim()] = value === SECRET_MASK ? SECRET_MASK : value;
			return changes;
		},
	};
}
