// Settings editor: the SettingKeys catalogue with live editing. The default-as-placeholder
// convention: an unset value renders as an EMPTY control with the default shown dimmed
// (input placeholder / "(default: X)" select option) plus a "default" badge — typing replaces
// it, clearing back to empty (or Reset) reverts to the default.

import { api } from '/shared/api.js';
import { h, render as renderInto, toast } from '/shared/ui.js';

export async function render(container) {
	const settings = await api('/admin/api/settings');

	// Group by the path segment after "ActiveSync:" ("ActiveSync:ReadOnly" → General).
	const groups = new Map();
	for (const s of settings) {
		const parts = s.key.split(':');
		const group = parts.length > 2 ? parts[1] : 'General';
		if (!groups.has(group)) groups.set(group, []);
		groups.get(group).push(s);
	}

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Settings'),
		h('div', { class: 'notice' },
			'Values apply to running gateways within ~1 s ("live") or at the next restart. ',
			'Empty fields use the dimmed default; clearing a field reverts to it.'),
		[...groups.entries()].map(([name, entries]) =>
			h('div', { class: 'card' },
				h('h2', {}, name),
				// Fixed layout with identical column widths in every group card, so the
				// controls line up across the whole page.
				h('table', { class: 'data', style: 'table-layout:fixed; width:100%' },
					h('tbody', {}, entries.map(settingRow))))));
}

function settingRow(setting) {
	const shortKey = setting.key.replace(/^ActiveSync:/, '');
	const control = buildControl(setting);
	const status = h('td', { style: 'width:86px' }, sourceBadge(setting.source));

	async function save(value) {
		try {
			if (value === '' || value === null) {
				const result = await api(`/admin/api/settings/${setting.key}`, { method: 'DELETE' });
				toast(`${shortKey} reset to default (${result.tier}).`, 'ok');
			} else {
				const result = await api(`/admin/api/settings/${setting.key}`, {
					method: 'PUT', body: { value: String(value) },
				});
				toast(result.tier === 'restart'
					? `${shortKey} saved — restart the gateway to apply.`
					: `${shortKey} saved (applies live).`, 'ok');
			}
			status.replaceChildren(sourceBadge(value === '' || value === null ? 'default' : 'db'));
		} catch (e) {
			toast(e.body?.error ?? `Saving ${shortKey} failed.`, 'error');
		}
	}

	control.addEventListener('change', () => save(control.value));

	return h('tr', {},
		h('td', { style: 'width:36%; vertical-align:top; padding-top:10px' },
			h('div', { class: 'mono' }, shortKey),
			h('div', { style: 'color:var(--fg-muted); font-size:12px' }, setting.help)),
		h('td', {}, control),
		status,
		h('td', { style: 'width:72px' }, h('span', { class: 'badge' }, setting.tier)),
		h('td', { style: 'width:44px; text-align:right' },
			h('button', { class: 'icon', title: 'Reset to default', onclick: () => {
				control.value = '';
				save(null);
			} }, '↺')));
}

function buildControl(setting) {
	const current = setting.source === 'default' ? '' : (setting.value ?? '');
	if (setting.type === 'Bool' || setting.enumValues) {
		const values = setting.enumValues ?? ['true', 'false'];
		// Configuration providers normalize JSON literals ("True"/"False", any enum casing) —
		// match case-insensitively or a set value would silently render as the default.
		const selected = values.find(v => v.toLowerCase() === current.toLowerCase()) ?? '';
		return h('select', {},
			h('option', { value: '', selected: selected === '' },
				`(default: ${setting.default ?? 'unset'})`),
			values.map(v => h('option', { value: v, selected: v === selected }, v)));
	}

	return h('input', {
		type: setting.secret ? 'password' : 'text',
		value: setting.secret ? '' : current,
		placeholder: setting.secret && current !== ''
			? '••• set — type to replace'
			: setting.default ?? '(unset)',
		spellcheck: 'false',
	});
}

function sourceBadge(source) {
	return source === 'db' ? h('span', { class: 'badge accent' }, 'db')
		: source === 'config' ? h('span', { class: 'badge' }, 'config')
		: h('span', { class: 'badge default' }, 'default');
}
