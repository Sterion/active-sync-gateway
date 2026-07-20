// State: live in-process gateway state — cached backend sessions, push watchers, parked
// long-polls — plus the /readyz component map. Auto-refreshes every 5 s while visible.

import { api } from '/shared/api.js';
import { h, render as renderInto, table } from '/shared/ui.js';

let refreshTimer = null;

export async function render(container) {
	if (refreshTimer !== null) {
		clearTimeout(refreshTimer);
		refreshTimer = null;
	}

	const host = h('div', {});
	renderInto(container, h('h1', { class: 'page-title' }, 'State'), host);
	await load(host);
}

async function load(host) {
	if (!document.body.contains(host)) return;
	const [state, ready] = await Promise.all([
		api('/admin/api/state'),
		fetch('/readyz').then(r => r.json()).catch(() => null),
	]);

	renderInto(host,
		h('div', { class: 'card' },
			h('h2', {}, 'Readiness'),
			ready === null
				? h('div', { class: 'notice' }, '/readyz is unreachable.')
				: h('div', { style: 'display:flex; gap:8px; flex-wrap:wrap' },
					Object.entries(ready.components ?? {}).map(([name, ok]) =>
						h('span', { class: ok ? 'badge ok' : 'badge danger' }, `${name}: ${ok ? 'ok' : 'DOWN'}`)))),
		h('div', { class: 'card' },
			h('h2', {}, `Backend sessions (${state.sessions.length})`),
			h('div', { class: 'notice' },
				'Cached backend connections per (user, device) — EAS is stateless HTTP, so this is ',
				'"recently active", not "phone online". Idle sessions evict automatically.'),
			state.sessions.length === 0
				? h('div', { style: 'color:var(--fg-muted)' }, 'No live sessions.')
				: table([
					{ label: 'User', cell: s => s.user },
					{ label: 'Device', cell: s => h('span', { class: 'mono' }, s.deviceId) },
					{ label: 'Last used (UTC)', cell: s => (s.lastUsedUtc ?? '').replace('T', ' ').slice(0, 19) },
				], state.sessions)),
		h('div', { class: 'card' },
			h('h2', {}, `Push watchers (${state.watchers.length})`),
			state.watchers.length === 0
				? h('div', { style: 'color:var(--fg-muted)' }, 'No live watchers.')
				: table([
					{ label: 'Provider', cell: w => w.provider },
					{ label: 'User', cell: w => w.user },
					{ label: 'Resource', cell: w => h('span', { class: 'mono' }, w.resource) },
				], state.watchers)),
		h('div', { class: 'card' },
			h('h2', {}, 'Parked long-polls'),
			state.longPolls.length === 0
				? h('div', { style: 'color:var(--fg-muted)' }, 'No Ping/Sync requests are parked right now.')
				: table([
					{ label: 'User', cell: p => p.user },
					{ label: 'Waiting requests', cell: p => String(p.count) },
				], state.longPolls)));

	refreshTimer = setTimeout(() => load(host), 5000);
}
