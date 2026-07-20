// Renders a backend provider's config fields from the schema it describes about itself, so
// no view here knows what an IMAP or CardDAV setting is called — the same module serves the
// Backends page, the admin user editor and the user portal.
//
// The default-as-placeholder convention holds throughout: an unset field renders EMPTY with
// the value it would inherit shown dimmed (input placeholder / "(default: X)" select option).
// Typing a value that equals the inherited one is therefore the same as not setting it, and
// collect() drops it — an override always means a real deviation.

import { h } from '/shared/ui.js';

export const SECRET_MASK = '***';

/**
 * A form for one provider+role.
 *   fields      — the schema array from /admin/api/backends/providers
 *   values      — {leaf: value} currently set at THIS level (may be empty)
 *   inherited   — {leaf: value} the level below supplies (config file / global role), optional
 *   sources     — {leaf: 'db'|'config'} for badges, optional
 * Returns {node, collect, markFailures}.
 *   collect() → {leaf: value|null} — only what changed: a cleared or inherited-equal field
 *   comes back as null, meaning "remove this override".
 */
export function schemaForm({ fields, values = {}, inherited = {}, sources = {} }) {
	const controls = [];
	const rows = fields.map(field => {
		const control = field.type === 'StringList'
			? listControl(field, values, inherited)
			: scalarControl(field, values, inherited);
		controls.push(control);

		const badge = sources[field.name] ? sourceBadge(sources[field.name]) : null;
		return h('div', { class: 'field' },
			h('label', {}, field.label,
				field.required ? h('span', { class: 'badge', style: 'margin-left:8px' }, 'required') : null,
				badge ? h('span', { style: 'margin-left:8px' }, badge) : null),
			control.node,
			field.help ? h('div', { class: 'field-help' }, field.help) : null,
			control.error);
	});

	return {
		node: h('div', { class: 'schema-form' }, rows),
		collect() {
			const changes = {};
			for (const control of controls) Object.assign(changes, control.collect());
			return changes;
		},
		/** failures: [{field, message}] from validate/save — attaches each to its input. */
		markFailures(failures) {
			for (const control of controls) control.error.replaceChildren();
			const unattached = [];
			for (const failure of failures ?? []) {
				const control = controls.find(c => c.name === failure.field);
				if (control) control.error.replaceChildren(failure.message);
				else unattached.push(failure.message);
			}
			return unattached;
		},
	};
}

/** The keys a schema claims, so callers can tell known fields from leftover raw ones. */
export function schemaKeys(fields) {
	return new Set((fields ?? []).map(f => f.name.toLowerCase()));
}

/** "SharedCollections:0" → "sharedcollections", for matching a leaf against schemaKeys. */
export function listRoot(key) {
	let root = key;
	for (;;) {
		const separator = root.lastIndexOf(':');
		if (separator < 0 || !/^\d+$/.test(root.slice(separator + 1))) return root.toLowerCase();
		root = root.slice(0, separator);
	}
}

export function sourceBadge(source) {
	return source === 'db' ? h('span', { class: 'badge accent' }, 'db')
		: source === 'config' ? h('span', { class: 'badge' }, 'config')
		: h('span', { class: 'badge default' }, 'default');
}

function scalarControl(field, values, inherited) {
	const set = values[field.name];
	const fallback = inherited[field.name] ?? field.default ?? '';
	// A masked secret is "already set"; showing the mask as a value would save it back verbatim.
	const isMaskedSecret = set === SECRET_MASK;
	const current = isMaskedSecret ? '' : (set ?? '');
	const error = h('div', { class: 'field-error' });

	let node;
	if (field.type === 'Bool' || field.enumValues) {
		const options = field.enumValues ?? ['true', 'false'];
		// Config providers normalize JSON literals ("True"/"False", any enum casing) — match
		// case-insensitively or a set value silently renders as the default.
		const selected = options.find(v => v.toLowerCase() === String(current).toLowerCase()) ?? '';
		node = h('select', {},
			h('option', { value: '', selected: selected === '' },
				`(default: ${fallback === '' ? 'unset' : fallback})`),
			options.map(v => h('option', { value: v, selected: v === selected }, v)));
	} else {
		node = h('input', {
			type: field.type === 'Secret' ? 'password' : 'text',
			inputmode: field.type === 'Int' ? 'numeric' : null,
			value: current,
			placeholder: isMaskedSecret
				? '••• set — type to replace'
				: (fallback === '' ? '(unset)' : fallback),
			spellcheck: 'false',
		});
	}

	return {
		name: field.name,
		node,
		error,
		collect() {
			const entered = node.value.trim();
			// Untouched secret: leave the stored value alone.
			if (isMaskedSecret && entered === '') return { [field.name]: SECRET_MASK };
			// Cleared, or exactly what the level below already supplies — store nothing.
			if (entered === '' || entered === String(fallback)) return { [field.name]: null };
			return { [field.name]: entered };
		},
	};
}

function listControl(field, values, inherited) {
	const prefix = field.name.toLowerCase() + ':';
	const existing = Object.keys(values)
		.filter(k => k.toLowerCase().startsWith(prefix))
		.sort((a, b) => Number(a.slice(prefix.length)) - Number(b.slice(prefix.length)))
		.map(k => values[k]);
	const inheritedCount = Object.keys(inherited).filter(k => k.toLowerCase().startsWith(prefix)).length;

	const error = h('div', { class: 'field-error' });
	const list = h('div', { class: 'list-field' });
	const entries = [];

	function draw() {
		list.replaceChildren(...entries.map((value, index) => {
			const input = h('input', { type: 'text', value, spellcheck: 'false' });
			input.addEventListener('input', () => { entries[index] = input.value; });
			return h('div', { class: 'list-row' }, input,
				h('button', {
					class: 'icon', title: 'Remove', type: 'button',
					onclick: () => { entries.splice(index, 1); draw(); },
				}, '✕'));
		}), h('button', {
			type: 'button',
			onclick: () => { entries.push(''); draw(); },
		}, 'Add'));
	}

	entries.push(...existing);
	draw();

	return {
		name: field.name,
		node: list,
		error,
		collect() {
			const kept = entries.map(e => e.trim()).filter(e => e !== '');
			const changes = {};
			for (let i = 0; i < kept.length; i++) changes[`${field.name}:${i}`] = kept[i];
			// Clear the indices this list used to have but no longer does — a shorter list must
			// not inherit the tail of the longer one it replaced.
			const stale = Math.max(existing.length, inheritedCount);
			for (let i = kept.length; i < stale; i++) changes[`${field.name}:${i}`] = null;
			return changes;
		},
	};
}
