// Devices: partnerships with block/unblock, account-only wipe (typed confirmation) and
// purge (typed confirmation). Mirrors `eas devices/block/unblock/device wipe/purge`.

import { api } from '/shared/api.js';
import { h, render as renderInto, table, toast } from '/shared/ui.js';

export async function render(container) {
	// Deep link: #/devices/<user> (e.g. from the dashboard) filters to that user.
	const userFilter = decodeURIComponent(location.hash.replace(/^#\/devices\/?/, ''));
	const devices = await api(userFilter
		? `/admin/api/devices?user=${encodeURIComponent(userFilter)}`
		: '/admin/api/devices');

	renderInto(container,
		h('h1', { class: 'page-title' }, 'Devices'),
		h('div', { class: 'card' },
			h('h2', {}, 'Partnerships',
				userFilter ? h('span', { class: 'badge accent', style: 'margin-left:8px' }, userFilter) : null,
				userFilter ? h('a', { href: '#/devices', style: 'margin-left:8px; font-size:12px' }, 'show all') : null),
			devices.length === 0
				? h('div', { class: 'notice' }, 'No devices have synced yet.')
				: table([
					{ label: 'User', cell: d => h('a', { href: `#/users/${encodeURIComponent(d.user)}` }, d.user) },
					{ label: 'Device', cell: d => h('span', { class: 'mono' }, d.deviceId) },
					{ label: 'Type', cell: d => d.deviceType || '—' },
					{ label: 'EAS', cell: d => d.lastProtocolVersion ?? '—' },
					{ label: 'Last seen (UTC)', cell: d => (d.lastSeenUtc ?? '').replace('T', ' ').slice(0, 16) },
					{ label: 'Status', cell: d => status(d) },
					{ label: 'Actions', cell: d => actions(d, container) },
				], devices)),
		h('div', { class: 'notice' },
			'Wipe = the 16.1 account-only directive (removes the account from the device, never a ',
			'factory reset). Purge = delete the gateway-side sync state. Block = 403 on login.'));
}

function status(d) {
	const badges = [];
	if (d.userDisabled) badges.push(h('span', { class: 'badge danger' }, 'user disabled'));
	else if (d.userBlocked) badges.push(h('span', { class: 'badge danger' }, 'user blocked'));
	else if (d.blocked) badges.push(h('span', { class: 'badge danger' }, 'blocked'));
	if (d.pendingAccountWipe) badges.push(h('span', { class: 'badge warn' }, 'wipe pending'));
	if (badges.length === 0) badges.push(h('span', { class: 'badge ok' }, 'active'));
	return h('span', { style: 'display:inline-flex; gap:6px' }, badges);
}

function actions(d, container) {
	const blockButton = h('button', {
		onclick: async () => {
			try {
				await api(`/admin/api/devices/${d.blocked && !d.userBlocked ? 'unblock' : 'block'}`,
					{ body: { user: d.user, deviceId: d.deviceId } });
				toast(d.blocked ? 'Unblocked.' : 'Blocked — logins now answer 403.', 'ok');
				refresh(container);
			} catch (e) {
				toast(e.body?.error ?? 'Block change failed.', 'error');
			}
		},
	}, d.blocked && !d.userBlocked ? 'Unblock' : 'Block');

	const wipeButton = h('button', {
		class: 'danger',
		onclick: () => d.pendingAccountWipe ? wipe(d, container, true) : confirmTyped(
			`Arm the ACCOUNT WIPE for ${d.user} / ${d.deviceId}? The account is removed from the ` +
			'device on its next request. Type the device id to confirm:',
			d.deviceId, () => wipe(d, container, false)),
	}, d.pendingAccountWipe ? 'Cancel wipe' : 'Wipe');

	const purgeButton = h('button', {
		class: 'danger',
		onclick: () => confirmTyped(
			`Permanently delete the gateway sync state of device ${d.deviceId}? ` +
			'(The next sync starts from scratch.) Type the device id to confirm:',
			d.deviceId, async confirm => {
				try {
					await api('/admin/api/devices/purge',
						{ body: { user: d.user, deviceId: d.deviceId, confirm } });
					toast('Device state purged.', 'ok');
					refresh(container);
				} catch (e) {
					toast(e.body?.error ?? 'Purge failed.', 'error');
				}
			}),
	}, 'Purge');

	return h('span', { style: 'display:inline-flex; gap:6px' }, blockButton, wipeButton, purgeButton);
}

async function wipe(d, container, cancel) {
	try {
		const result = await api('/admin/api/devices/wipe',
			{ body: { user: d.user, deviceId: d.deviceId, cancel, confirm: cancel ? null : d.deviceId } });
		toast(cancel ? 'Pending wipe cancelled.' : 'Account wipe armed.', 'ok');
		if (result.warning) toast(result.warning, 'error');
		refresh(container);
	} catch (e) {
		toast(e.body?.error ?? 'Wipe change failed.', 'error');
	}
}

/** Destructive confirm: the user must type the expected value back. */
function confirmTyped(message, expected, onConfirmed) {
	const input = h('input', { placeholder: expected, spellcheck: 'false' });
	const dialog = h('dialog', { class: 'card' },
		h('p', {}, message),
		input,
		h('div', { style: 'display:flex; gap:8px; justify-content:flex-end; margin-top:12px' },
			h('button', { onclick: () => dialog.close() }, 'Cancel'),
			h('button', {
				class: 'danger',
				onclick: () => {
					if (input.value !== expected) {
						toast('The confirmation text does not match.', 'error');
						return;
					}
					dialog.close();
					onConfirmed(input.value);
				},
			}, 'Confirm')));
	dialog.addEventListener('close', () => dialog.remove());
	document.body.append(dialog);
	dialog.showModal();
}

function refresh(container) {
	import('/admin/views/devices.js').then(m => m.render(container));
}
