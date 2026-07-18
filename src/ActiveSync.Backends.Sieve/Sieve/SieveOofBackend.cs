using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Sieve;

/// <summary>
///   IOofBackend over ManageSieve: renders the gateway-owned vacation script from the
///   semantic reply, activates it, and restores whatever was active before on disable.
///   The restore token is the previously active script name. Sieve vacation carries plain
///   text — an HTML reply body travels as-is. Connects per operation — Oof changes are
///   rare and a pooled sieve connection would only rot.
/// </summary>
public sealed class SieveOofBackend(
	SieveOptions options,
	BackendCredentials credentials,
	ILogger? wireLogger = null) : IOofBackend
{
	public async Task<string?> EnableAsync(OofReply reply, CancellationToken ct)
	{
		string script = SieveVacationScript.Build(reply.BodyText, reply.StartUtc, reply.EndUtc);
		await using ManageSieveClient client = new(options, credentials, wireLogger);
		await client.ConnectAsync(ct).ConfigureAwait(false);

		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(ct).ConfigureAwait(false);
		string previousActive = scripts.FirstOrDefault(s => s.Active).Name ?? "";

		await client.PutScriptAsync(SieveVacationScript.ScriptName, script, ct).ConfigureAwait(false);
		await client.SetActiveAsync(SieveVacationScript.ScriptName, ct).ConfigureAwait(false);
		// Re-arm: our own script was already active — the caller's stored token still
		// names the right thing to restore.
		return previousActive == SieveVacationScript.ScriptName ? null : previousActive;
	}

	public async Task DisableAsync(string restoreToken, CancellationToken ct)
	{
		await using ManageSieveClient client = new(options, credentials, wireLogger);
		await client.ConnectAsync(ct).ConfigureAwait(false);

		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(ct).ConfigureAwait(false);

		// Restore the pre-Oof active script when it still exists; otherwise just deactivate.
		bool restorable = restoreToken.Length > 0 &&
		                  restoreToken != SieveVacationScript.ScriptName &&
		                  scripts.Any(s => s.Name == restoreToken);
		await client.SetActiveAsync(restorable ? restoreToken : "", ct).ConfigureAwait(false);

		if (scripts.Any(s => s.Name == SieveVacationScript.ScriptName))
			await client.DeleteScriptAsync(SieveVacationScript.ScriptName, ct).ConfigureAwait(false);
	}
}
