// Dashboard view — health/summary tiles arrive with the state API (Phase 4); this shell
// version shows the signed-in session so the flow is verifiable end-to-end.

import { api } from '/shared/api.js';
import { h } from '/shared/ui.js';

export async function render(container) {
	const session = await api('/admin/api/session');
	container.replaceChildren(
		h('h1', { class: 'page-title' }, 'Dashboard'),
		h('div', { class: 'card' },
			h('h2', {}, 'Session'),
			h('p', {}, 'Signed in as ', h('strong', {}, session.login ?? '?'),
				session.admin ? h('span', { class: 'badge accent', style: 'margin-left:8px' }, 'admin') : null)),
		h('div', { class: 'notice' },
			'Gateway health, sessions and summaries land here in an upcoming phase.'));
}
