// Dashboard: readiness + summary tiles + a peek at live activity. Rate/latency charts stay
// in Prometheus/Grafana — this is the "is everything alright" page.

import { api } from '/shared/api.js';
import { h, render as renderInto } from '/shared/ui.js';

export async function render(container) {
	const [session, summary, state, ready] = await Promise.all([
		api('/admin/api/session'),
		api('/admin/api/summary'),
		api('/admin/api/state'),
		fetch('/readyz').then(r => r.json()).catch(() => null),
	]);

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Dashboard'),
		h('div', { class: 'card' },
			h('h2', {}, 'Health'),
			ready === null
				? h('div', { class: 'notice' }, '/readyz is unreachable.')
				: h('div', { style: 'display:flex; gap:8px; flex-wrap:wrap' },
					Object.entries(ready.components ?? {}).map(([name, ok]) =>
						h('span', { class: ok ? 'badge ok' : 'badge danger' }, `${name}: ${ok ? 'ok' : 'DOWN'}`)))),
		h('div', { style: 'display:grid; grid-template-columns:repeat(auto-fill, minmax(170px, 1fr)); gap:14px' },
			tile('Declared users', summary.declaredUsers),
			tile('Users with devices', summary.deviceUsers),
			tile('Devices', summary.devices),
			tile('Live sessions', state.sessions.length),
			tile('Push watchers', state.watchers.length),
			tile('Parked long-polls', state.longPolls.reduce((sum, p) => sum + p.count, 0)),
			tile('Login blocks', summary.blocks, summary.blocks > 0 ? 'warn' : ''),
			tile('Pending wipes', summary.pendingWipes, summary.pendingWipes > 0 ? 'warn' : ''),
			tile('Errors (1 h)', summary.errorsLastHour, summary.errorsLastHour > 0 ? 'danger' : ''),
			tile('Warnings (1 h)', summary.warningsLastHour, summary.warningsLastHour > 0 ? 'warn' : '')),
		h('div', { class: 'notice', style: 'margin-top:16px' },
			'Signed in as ', h('strong', {}, session.login ?? '?'),
			'. Request-rate and latency charts live in Prometheus/Grafana (ActiveSync:Metrics).'));
}

function tile(label, value, tone = '') {
	return h('div', { class: 'card', style: 'margin-bottom:0' },
		h('div', { style: 'color:var(--fg-muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em' }, label),
		h('div', {
			style: `font-size:26px; font-weight:600; margin-top:4px;` +
				(tone === 'danger' ? 'color:var(--danger)' : tone === 'warn' ? 'color:var(--warn)' : ''),
		}, String(value ?? 0)));
}
