// Dashboard: health badges + a compact counters panel (top right). Rate/latency charts stay
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
		h('div', { style: 'display:grid; grid-template-columns:minmax(0,1fr) 300px; gap:16px; align-items:start' },
			h('div', {},
				h('div', { class: 'card' },
					h('h2', {}, 'Health'),
					ready === null
						? h('div', { class: 'notice' }, '/readyz is unreachable.')
						: h('div', { style: 'display:flex; gap:8px; flex-wrap:wrap' },
							Object.entries(ready.components ?? {}).map(([name, ok]) =>
								h('span', { class: ok ? 'badge ok' : 'badge danger' }, `${name}: ${ok ? 'ok' : 'DOWN'}`)))),
				h('div', { class: 'notice' },
					'Signed in as ', h('strong', {}, session.login ?? '?'),
					'. Request-rate and latency charts live in Prometheus/Grafana (ActiveSync:Metrics).')),
			h('div', { class: 'card', style: 'margin-bottom:0' },
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
				stat('Warnings (1 h)', summary.warningsLastHour, summary.warningsLastHour > 0 ? 'warn' : ''))));
}

function stat(label, value, tone = '') {
	return h('div', { class: 'stat-row' },
		h('span', {}, label),
		h('strong', {
			style: tone === 'danger' ? 'color:var(--danger)' : tone === 'warn' ? 'color:var(--warn)' : '',
		}, String(value ?? 0)));
}
