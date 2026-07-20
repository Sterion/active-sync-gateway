// Logs: history + live tail over the shared LogEntries table. Live mode polls
// ?after=<lastId> every 2 s (the autoincrement Id is the multi-pod-correct cursor; the
// Machine column tags the replica). Filters apply to both modes.

import { api } from '/shared/api.js';
import { h, render as renderInto } from '/shared/ui.js';

let pollTimer = null;

export async function render(container) {
	stopPolling();

	const level = h('select', {},
		['Information', 'Warning', 'Error'].map(l =>
			h('option', { value: l === 'Information' ? '' : l }, l === 'Information' ? 'All levels' : `${l}+`)));
	const user = h('input', { placeholder: 'user', spellcheck: 'false', style: 'width:130px' });
	const text = h('input', { placeholder: 'text search (message + exception)', spellcheck: 'false' });
	const machine = h('input', { placeholder: 'pod/machine', spellcheck: 'false', style: 'width:130px' });
	const live = h('button', { class: 'primary' }, 'Live tail');
	const refresh = h('button', {}, 'Refresh');
	const body = h('tbody', {});
	let lastId = 0;
	let isLive = false;

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Logs'),
		h('div', { class: 'card' },
			h('div', { style: 'display:flex; gap:8px; align-items:center; flex-wrap:wrap; margin-bottom:12px' },
				level, user, machine, text, refresh, live),
			h('table', { class: 'data' },
				h('thead', {}, h('tr', {},
					h('th', { style: 'width:135px' }, 'Time (UTC)'),
					h('th', { style: 'width:70px' }, 'Level'),
					h('th', {}, 'Message'),
					h('th', { style: 'width:110px' }, 'User'),
					h('th', { style: 'width:110px' }, 'Source'))),
				body)));

	refresh.addEventListener('click', () => loadHistory());
	live.addEventListener('click', () => {
		isLive = !isLive;
		live.textContent = isLive ? 'Pause' : 'Live tail';
		live.classList.toggle('primary', !isLive);
		if (isLive) poll();
		else stopPolling();
	});
	for (const control of [level, user, machine, text])
		control.addEventListener('change', () => loadHistory());

	await loadHistory();

	function query(extra) {
		const params = new URLSearchParams(extra);
		if (level.value) params.set('level', level.value);
		if (user.value.trim()) params.set('user', user.value.trim());
		if (machine.value.trim()) params.set('machine', machine.value.trim());
		if (text.value.trim()) params.set('text', text.value.trim());
		return `/admin/api/logs?${params}`;
	}

	async function loadHistory() {
		const result = await api(query({ sinceMinutes: '1440', limit: '200' }));
		lastId = result.lastId;
		body.replaceChildren(...result.entries.map(row)); // newest first
	}

	async function poll() {
		if (!isLive || !document.body.contains(body)) { stopPolling(); return; }
		try {
			const result = await api(query({ after: String(lastId), limit: '200' }));
			if (result.entries.length > 0) {
				lastId = result.lastId;
				body.prepend(...result.entries.map(row).reverse()); // tail is chronological
				while (body.children.length > 500)
					body.lastChild.remove();
			}
		} catch { /* transient poll errors: keep tailing */ }
		pollTimer = setTimeout(poll, 2000);
	}

	function row(entry) {
		const levelClass = entry.level === 'Error' || entry.level === 'Fatal' ? 'danger'
			: entry.level === 'Warning' ? 'warn' : '';
		return h('tr', {},
			h('td', { class: 'mono' }, (entry.timestampUtc ?? '').replace('T', ' ').slice(0, 19)),
			h('td', {}, h('span', { class: `badge ${levelClass}` }, entry.level)),
			h('td', {},
				h('div', {}, entry.message),
				entry.exception ? h('pre', { class: 'mono', style: 'white-space:pre-wrap; color:var(--danger); margin:4px 0 0' },
					entry.exception) : null,
				entry.machine ? h('span', { class: 'badge', title: 'replica' }, entry.machine) : null),
			h('td', {}, entry.user ?? ''),
			h('td', { class: 'mono', title: entry.sourceContext ?? '' },
				(entry.sourceContext ?? '').split('.').pop() ?? ''));
	}
}

function stopPolling() {
	if (pollTimer !== null) {
		clearTimeout(pollTimer);
		pollTimer = null;
	}
}
