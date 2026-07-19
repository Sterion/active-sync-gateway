// Fetch wrapper for the gateway's JSON API. Adds the CSRF companion header on every call and
// funnels 401 into an app-wide event so the shell can drop back to the login view.

export class ApiError extends Error {
	constructor(status, body) {
		super(`API ${status}`);
		this.status = status;
		this.body = body;
	}
}

export async function api(path, options = {}) {
	const init = {
		method: options.method ?? (options.body !== undefined ? 'POST' : 'GET'),
		headers: { 'X-EAS-WebUi': '1', ...(options.headers ?? {}) },
	};
	if (options.body !== undefined) {
		init.headers['Content-Type'] = 'application/json';
		init.body = JSON.stringify(options.body);
	}

	const response = await fetch(path, init);
	if (response.status === 401) {
		document.dispatchEvent(new CustomEvent('eas:unauthorized'));
		throw new ApiError(401, null);
	}
	let body = null;
	const text = await response.text();
	if (text) {
		try { body = JSON.parse(text); } catch { body = text; }
	}
	if (!response.ok)
		throw new ApiError(response.status, body);
	return body;
}
