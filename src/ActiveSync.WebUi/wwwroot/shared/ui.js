// Tiny DOM helpers — the whole "framework" of this no-build SPA.

/** h('button', {class: 'primary', onclick: fn}, 'Save') → HTMLElement */
export function h(tag, attrs = {}, ...children) {
	const element = document.createElement(tag);
	for (const [name, value] of Object.entries(attrs)) {
		if (value === null || value === undefined || value === false) continue;
		if (name.startsWith('on') && typeof value === 'function')
			element.addEventListener(name.slice(2), value);
		else if (name === 'dataset')
			Object.assign(element.dataset, value);
		else if (value === true)
			element.setAttribute(name, '');
		else
			element.setAttribute(name, value);
	}
	element.append(...children.flat().filter(c => c !== null && c !== undefined));
	return element;
}

/** Replaces a container's children. */
export function render(container, ...children) {
	container.replaceChildren(...children.flat().filter(c => c !== null && c !== undefined));
}

/** Simple data table: columns = [{label, cell(row) → node|string}] */
export function table(columns, rows) {
	return h('table', { class: 'data' },
		h('thead', {}, h('tr', {}, columns.map(c => h('th', {}, c.label)))),
		h('tbody', {}, rows.map(row =>
			h('tr', {}, columns.map(c => h('td', {}, c.cell(row)))))));
}

/** Toast notification; kind: 'info' | 'ok' | 'error'. */
export function toast(message, kind = 'info') {
	let host = document.getElementById('toast');
	if (!host) {
		host = h('div', { id: 'toast' });
		document.body.append(host);
	}
	const item = h('div', { class: `item ${kind}` }, message);
	host.append(item);
	setTimeout(() => item.remove(), kind === 'error' ? 8000 : 4000);
}

/**
 * The default-as-placeholder convention: an input whose value is NOT set renders empty with
 * the default (or inherited value) as a dimmed placeholder + a "default" badge next to the
 * label; typing replaces it naturally, clearing back to empty reverts to the default.
 */
export function fieldRow({ label, value, placeholder, badge, type = 'text', oninput }) {
	const input = h('input', {
		type,
		value: value ?? '',
		placeholder: placeholder ?? '',
		spellcheck: 'false',
	});
	if (oninput) input.addEventListener('input', () => oninput(input.value));
	const labelRow = h('label', {}, label,
		badge && (value === null || value === undefined || value === '')
			? h('span', { class: 'badge default', style: 'margin-left:8px' }, badge)
			: null);
	return { row: h('div', {}, labelRow, input), input };
}

/** Confirmation dialog (native <dialog>); resolves true when confirmed. */
export function confirmDialog(message, confirmLabel = 'Confirm') {
	return new Promise(resolve => {
		const dialog = h('dialog', { class: 'card' },
			h('p', {}, message),
			h('div', { style: 'display:flex; gap:8px; justify-content:flex-end' },
				h('button', { onclick: () => { dialog.close(); resolve(false); } }, 'Cancel'),
				h('button', {
					class: 'danger',
					onclick: () => { dialog.close(); resolve(true); },
				}, confirmLabel)));
		dialog.addEventListener('close', () => dialog.remove());
		document.body.append(dialog);
		dialog.showModal();
	});
}
