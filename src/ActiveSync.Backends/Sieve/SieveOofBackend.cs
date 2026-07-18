using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Sieve;

/// <summary>
///   IOofBackend over ManageSieve: uploads/activates the gateway-owned vacation script and
///   restores whatever was active before on disable. Connects per operation — Oof changes
///   are rare and a pooled sieve connection would only rot.
/// </summary>
public sealed class SieveOofBackend(
	SieveOptions options,
	BackendCredentials credentials,
	ILogger? wireLogger = null) : IOofBackend
{
	public async Task<string> ActivateAsync(string sieveScript, CancellationToken ct)
	{
		await using ManageSieveClient client = new(options, credentials, wireLogger);
		await client.ConnectAsync(ct).ConfigureAwait(false);

		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(ct).ConfigureAwait(false);
		string previousActive = scripts.FirstOrDefault(s => s.Active).Name ?? "";

		await client.PutScriptAsync(SieveVacationScript.ScriptName, sieveScript, ct).ConfigureAwait(false);
		await client.SetActiveAsync(SieveVacationScript.ScriptName, ct).ConfigureAwait(false);
		return previousActive;
	}

	public async Task DeactivateAsync(string previousActiveScript, CancellationToken ct)
	{
		await using ManageSieveClient client = new(options, credentials, wireLogger);
		await client.ConnectAsync(ct).ConfigureAwait(false);

		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(ct).ConfigureAwait(false);

		// Restore the pre-Oof active script when it still exists; otherwise just deactivate.
		bool restorable = previousActiveScript.Length > 0 &&
		                  previousActiveScript != SieveVacationScript.ScriptName &&
		                  scripts.Any(s => s.Name == previousActiveScript);
		await client.SetActiveAsync(restorable ? previousActiveScript : "", ct).ConfigureAwait(false);

		if (scripts.Any(s => s.Name == SieveVacationScript.ScriptName))
			await client.DeleteScriptAsync(SieveVacationScript.ScriptName, ct).ConfigureAwait(false);
	}
}
