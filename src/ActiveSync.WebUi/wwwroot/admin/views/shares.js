// Shared-calendar grants — mirrors `eas share list/add/remove`.

import { api } from '/shared/api.js';
import { h, render as renderInto, table, toast, confirmDialog } from '/shared/ui.js';

export async function render(container) {
	// Paged like /devices — see C10; the endpoint caps a page at 500 rows.
	const page = await api('/admin/api/shares');
	const shares = page.entries;

	const user = h('input', { placeholder: 'gateway login', spellcheck: 'false' });
	const href = h('input', { placeholder: '/dav/cal/family/ (absolute CalDAV collection path)', spellcheck: 'false' });
	const readOnly = h('input', { type: 'checkbox', style: 'width:auto' });

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Shared calendars'),
		h('div', { class: 'card' },
			h('h2', {}, 'Grants'),
			shares.length === 0
				? h('div', { class: 'notice' }, 'No shared-calendar grants.')
				: table([
					{ label: 'User', cell: s => s.user },
					{ label: 'Collection', cell: s => h('span', { class: 'mono' }, s.collectionHref) },
					{ label: 'Mode', cell: s => s.readOnly
						? h('span', { class: 'badge warn' }, 'read-only')
						: h('span', { class: 'badge ok' }, 'read-write') },
					{ label: 'Granted (UTC)', cell: s => s.createdUtc?.replace('T', ' ').slice(0, 16) ?? '' },
					{ label: '', cell: s => h('button', { class: 'danger', onclick: () => remove(s) }, 'Remove') },
				], shares),
			shares.length < page.total
				? h('div', { class: 'notice' }, `Showing ${shares.length} of ${page.total} grants.`)
				: null),
		h('div', { class: 'card' },
			h('h2', {}, 'Grant a collection'),
			h('label', {}, 'User'), user,
			h('label', {}, 'Collection path (must live on the user’s CalDAV server)'), href,
			h('label', { style: 'display:flex; align-items:center; gap:8px' }, readOnly,
				'Read-only (client edits in this calendar are silently reverted)'),
			h('button', { class: 'primary', style: 'margin-top:10px', onclick: add }, 'Grant'),
			h('div', { class: 'notice', style: 'margin-top:12px' },
				'Grants apply when the user’s backend session is next built (idle recycle or restart).')));

	async function add() {
		try {
			await api('/admin/api/shares', {
				body: { user: user.value.trim(), collectionHref: href.value.trim(), readOnly: readOnly.checked },
			});
			toast('Grant saved.', 'ok');
			refresh(container);
		} catch (e) {
			toast(e.body?.error ?? 'Granting failed.', 'error');
		}
	}

	async function remove(share) {
		if (!await confirmDialog(`Remove ${share.collectionHref} from ${share.user}?`, 'Remove')) return;
		await api(`/admin/api/shares?user=${encodeURIComponent(share.user)}&collectionHref=${encodeURIComponent(share.collectionHref)}`,
			{ method: 'DELETE' });
		toast('Grant removed.', 'ok');
		refresh(container);
	}
}

function refresh(container) {
	import('/admin/views/shares.js').then(m => m.render(container));
}
