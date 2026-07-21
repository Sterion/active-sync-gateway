// Dashboard: the RUNNING overview — health, who is connected right now (users + their
// devices, with push indicators and deep links), a small live log tail, and the compact
// counters panel. Auto-refreshes every 3 s while visible. Rate/latency charts stay in
// Prometheus/Grafana.

import { api } from '/shared/api.js';
import { h, render as renderInto } from '/shared/ui.js';

let refreshTimer = null;

export async function render(container) {
	if (refreshTimer !== null) {
		clearTimeout(refreshTimer);
		refreshTimer = null;
	}

	const host = h('div', {});
	renderInto(container, h('h1', { class: 'page-title' }, 'Dashboard'), host);
	await load(host);
}

async function load(host) {
	if (!document.body.contains(host)) return;
	const [summary, state, devices, logs, ready] = await Promise.all([
		api('/admin/api/summary'),
		api('/admin/api/state'),
		// Paged (C10): the "active now" card only annotates users that already have a live
		// session, so a page of device metadata is all it can use.
		api('/admin/api/devices').then(page => page.entries),
		api('/admin/api/logs?sinceMinutes=1440&limit=20'),
		fetch('/readyz').then(r => r.json()).catch(() => null),
	]);

	renderInto(host,
		h('div', { style: 'display:grid; grid-template-columns:minmax(0,1fr) 300px; gap:16px; align-items:start' },
			h('div', {},
				h('div', { class: 'card' },
					h('h2', {}, 'Health'),
					ready === null
						? h('div', { class: 'notice' }, '/readyz is unreachable.')
						: h('div', { style: 'display:flex; gap:8px; flex-wrap:wrap' },
							Object.entries(ready.components ?? {}).map(([name, ok]) =>
								h('span', { class: ok ? 'badge ok' : 'badge danger' }, `${name}: ${ok ? 'ok' : 'DOWN'}`)))),
				activeNowCard(state, devices),
				liveLogCard(logs)),
			countersCard(summary, state)));

	refreshTimer = setTimeout(() => load(host), 3000);
}

/* ---- Active now: users with live sessions/push, their devices, deep links -------------- */

function activeNowCard(state, devices) {
	// One entry per user that has ANY live sign: a cached session, a push watcher or a
	// parked long-poll. Sessions carry the device ids; device metadata fills in the rest.
	const users = new Map();
	const entry = user => {
		if (!users.has(user))
			users.set(user, { sessions: [], watchers: 0, longPolls: 0 });
		return users.get(user);
	};
	for (const session of state.sessions) entry(session.user).sessions.push(session);
	for (const watcher of state.watchers) entry(watcher.user).watchers++;
	for (const poll of state.longPolls) entry(poll.user).longPolls = poll.count;

	const deviceInfo = new Map(devices.map(d => [`${d.user}\n${d.deviceId}`, d]));

	return h('div', { class: 'card' },
		h('h2', {}, `Active now (${users.size})`),
		users.size > 0
			? [...users.entries()]
				.sort((a, b) => a[0].localeCompare(b[0]))
				.map(([user, info]) => userRow(user, info, deviceInfo))
			: [
				h('div', { style: 'color:var(--fg-muted); margin-bottom:8px' },
					'Nothing is connected right now — users appear here while a device syncs, ',
					'holds a push long-poll or keeps a cached backend session (~15 min).'),
				// Not live, but recent: the last partnerships that talked to the gateway.
				...(devices.length === 0 ? [] : [
					h('h2', { style: 'margin-top:14px' }, 'Recently seen'),
					...devices
						.slice()
						.sort((a, b) => (b.lastSeenUtc ?? '').localeCompare(a.lastSeenUtc ?? ''))
						.slice(0, 5)
						.map(d => h('div', { style: 'display:flex; gap:10px; align-items:baseline; padding:2px 0; font-size:12.5px' },
							h('a', { href: `#/users/${encodeURIComponent(d.user)}`, style: 'font-weight:600' }, d.user),
							h('a', { class: 'mono', href: `#/devices/${encodeURIComponent(d.user)}` }, d.deviceId),
							h('span', { style: 'color:var(--fg-muted)' }, d.deviceType || 'unknown'),
							h('span', { style: 'color:var(--fg-faint); margin-left:auto' }, `seen ${ago(d.lastSeenUtc)}`))),
				]),
			]);
}

function userRow(user, info, deviceInfo) {
	const badges = [];
	if (info.longPolls > 0)
		badges.push(h('span', { class: 'badge ok', title: 'parked Ping/Sync waits' }, `push ×${info.longPolls}`));
	if (info.watchers > 0)
		badges.push(h('span', { class: 'badge accent', title: 'live IDLE watchers' }, `idle ×${info.watchers}`));

	return h('div', { style: 'padding:7px 0; border-bottom:1px solid var(--border)' },
		h('div', { style: 'display:flex; align-items:center; gap:8px' },
			h('a', { href: `#/users/${encodeURIComponent(user)}`, style: 'font-weight:600' }, user),
			...badges),
		info.sessions.length === 0
			? h('div', { style: 'color:var(--fg-muted); font-size:12.5px; padding-left:14px' },
				'push only — no cached session')
			: info.sessions.map(session => {
				const device = deviceInfo.get(`${session.user}\n${session.deviceId}`);
				return h('div', { style: 'display:flex; gap:10px; align-items:baseline; padding:2px 0 2px 14px; font-size:12.5px' },
					h('a', { class: 'mono', href: `#/devices/${encodeURIComponent(user)}` }, session.deviceId),
					device ? h('span', { style: 'color:var(--fg-muted)' }, device.deviceType || 'unknown') : null,
					device?.lastProtocolVersion ? h('span', { class: 'badge' }, `EAS ${device.lastProtocolVersion}`) : null,
					h('span', { style: 'color:var(--fg-faint); margin-left:auto' },
						`active ${ago(session.lastUsedUtc)}`));
			}));
}

/* ---- Live log tail ----------------------------------------------------------------------- */

function liveLogCard(logs) {
	return h('div', { class: 'card' },
		h('div', { style: 'display:flex; justify-content:space-between; align-items:baseline; margin-bottom:8px' },
			h('h2', { style: 'margin:0' }, 'Live log'),
			h('a', { href: '#/logs' }, 'open logs →')),
		logs.entries.length === 0
			? h('div', { style: 'color:var(--fg-muted)' }, 'No log entries yet.')
			: h('table', { class: 'data compact', style: 'table-layout:fixed; width:100%' },
				h('tbody', {}, logs.entries.map(entry => {
					const levelClass = entry.level === 'Error' || entry.level === 'Fatal' ? 'danger'
						: entry.level === 'Warning' ? 'warn' : '';
					return h('tr', {},
						h('td', { class: 'mono', style: 'width:96px' },
							(entry.timestampUtc ?? '').replace('T', ' ').slice(11, 19)),
						h('td', { style: 'width:64px' }, h('span', { class: `badge ${levelClass}` }, entry.level.slice(0, 4))),
						h('td', { class: 'ellipsis', title: entry.message }, entry.message));
				}))));
}

/* ---- Counters ----------------------------------------------------------------------------- */

function countersCard(summary, state) {
	return h('div', { class: 'card', style: 'margin-bottom:0' },
		h('h2', {}, 'Counters'),
		stat('Declared users', summary.declaredUsers),
		stat('Users with devices', summary.deviceUsers),
		stat('Devices', summary.devices),
		stat('Live sessions', state.sessions.length),
		stat('Push watchers', state.watchers.length),
		stat('Parked long-polls', state.longPolls.reduce((sum, p) => sum + p.count, 0)),
		stat('Login blocks', summary.blocks, summary.blocks > 0 ? 'warn' : ''),
		stat('Pending wipes', summary.pendingWipes, summary.pendingWipes > 0 ? 'warn' : ''),
		stat('Errors (1 h)', summary.errorsLastHour, summary.errorsLastHour > 0 ? 'danger' : ''),
		stat('Warnings (1 h)', summary.warningsLastHour, summary.warningsLastHour > 0 ? 'warn' : ''));
}

function stat(label, value, tone = '') {
	return h('div', { class: 'stat-row' },
		h('span', {}, label),
		h('strong', {
			style: tone === 'danger' ? 'color:var(--danger)' : tone === 'warn' ? 'color:var(--warn)' : '',
		}, String(value ?? 0)));
}

function ago(iso) {
	if (!iso) return '';
	const seconds = Math.max(0, (Date.now() - new Date(iso + (iso.endsWith('Z') ? '' : 'Z')).getTime()) / 1000);
	if (seconds < 60) return `${Math.round(seconds)}s ago`;
	if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
	return `${Math.round(seconds / 3600)}h ago`;
}
