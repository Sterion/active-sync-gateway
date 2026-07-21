// TLS: the gateway's active HTTPS certificate — which mode is serving (self-signed / mounted
// file / off), the certificate's details, and how to change it. Read-only: the certificate
// paths are edited on the Settings page (ActiveSync:Tls:*), restart-tier.

import { api } from '/shared/api.js';
import { h, render as renderInto } from '/shared/ui.js';

const SOURCE_LABEL = {
	Disabled: 'Disabled — TLS terminated in front of the gateway',
	SelfSigned: 'Self-signed (generated and stored in the database)',
	External: 'Mounted certificate (operator-supplied file)',
};

function daysUntil(iso) {
	if (!iso) return null;
	return Math.floor((new Date(iso).getTime() - Date.now()) / 86400000);
}

function fmt(iso) {
	return iso ? iso.replace('T', ' ').slice(0, 19) + ' UTC' : '—';
}

function row(label, value) {
	return h('div', { style: 'display:grid; grid-template-columns:180px 1fr; gap:8px; padding:4px 0' },
		h('div', { style: 'color:var(--fg-muted)' }, label),
		h('div', {}, value ?? '—'));
}

export async function render(container) {
	const tls = await api('/admin/api/tls');
	const host = h('div', {});
	renderInto(container, h('h1', { class: 'page-title' }, 'TLS'), host);

	const cards = [
		h('div', { class: 'card' },
			h('h2', {}, 'HTTPS'),
			row('Status', tls.enabled
				? h('span', { class: 'badge ok' }, `serving on :${tls.port}`)
				: h('span', { class: 'badge' }, 'off')),
			row('Source', SOURCE_LABEL[tls.source] ?? tls.source),
			tls.source === 'External' ? row('Certificate file', h('span', { class: 'mono' }, tls.certificatePath)) : null),
	];

	if (tls.error) {
		cards.push(h('div', { class: 'card' },
			h('h2', {}, 'Problem'),
			h('div', { class: 'notice danger' }, tls.error)));
	}

	if (tls.fingerprint) {
		const days = daysUntil(tls.notAfterUtc);
		const expiry = days === null ? null
			: days < 0 ? h('span', { class: 'badge danger' }, `expired ${-days} day(s) ago`)
				: days <= 30 ? h('span', { class: 'badge danger' }, `expires in ${days} day(s)`)
					: h('span', { class: 'badge ok' }, `valid for ${days} day(s)`);
		cards.push(h('div', { class: 'card' },
			h('h2', {}, 'Certificate'),
			row('Subject', h('span', { class: 'mono' }, tls.subject)),
			row('Issuer', h('span', { class: 'mono' }, tls.issuer)),
			row('Subject alt names', (tls.subjectAlternativeNames ?? []).length
				? h('span', { class: 'mono' }, tls.subjectAlternativeNames.join(', ')) : '—'),
			row('Valid from', fmt(tls.notBeforeUtc)),
			row('Valid until', h('span', {}, fmt(tls.notAfterUtc), expiry ? ' ' : '', expiry ?? '')),
			row('Key', tls.keyAlgorithm ? `${tls.keyAlgorithm}${tls.keySize ? ` ${tls.keySize}-bit` : ''}` : '—'),
			row('SHA-256', h('span', { class: 'mono', style: 'word-break:break-all' }, tls.fingerprint))));
	}

	cards.push(h('div', { class: 'card' },
		h('h2', {}, 'Changing the certificate'),
		h('div', { class: 'notice' },
			'To serve your own certificate (e.g. from cert-manager/ACME), mount it and set ',
			h('span', { class: 'mono' }, 'ActiveSync:Tls:CertificatePath'),
			' (a PEM full chain — pair it with ',
			h('span', { class: 'mono' }, 'ActiveSync:Tls:CertificateKeyPath'),
			' — or a PFX bundle) on the ',
			h('a', { href: '#/settings' }, 'Settings'),
			' page. Leave it unset to use the self-signed certificate. These are read once at ',
			'startup, so the gateway must be restarted after changing them or after the mounted ',
			'certificate rotates.')));

	renderInto(host, ...cards.filter(Boolean));
}
