using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using Microsoft.AspNetCore.Http;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The two things every endpoint in this folder does the same way, written once.
///
///   <b>The error shape is a contract, not a convention.</b> Every handler answers a rejected
///   request with <c>{ error: string }</c>, optionally alongside
///   <c>{ failures: [{ field, message }] }</c> when the failure attaches to specific inputs —
///   the SPA reads exactly those two members (<c>e.body?.error</c> in every view,
///   <c>form.markFailures(failures)</c> in the schema forms). It is deliberately NOT
///   <c>ProblemDetails</c>: this is a same-origin JSON API for one hand-written client, and the
///   RFC 7807 envelope would buy nothing the SPA reads.
/// </summary>
internal static class EndpointHelpers
{
	/// <summary>
	///   Parses a role route segment. Returns false with <paramref name="error" /> set to a
	///   ready 400 — one message for all five routes that take a role.
	/// </summary>
	internal static bool TryParseRole(string? value, out BackendRole role, out IResult? error)
	{
		if (Enum.TryParse(value, true, out role) && Enum.IsDefined(role))
		{
			error = null;
			return true;
		}

		error = BadRequest(
			$"'{value}' is not a backend role (roles: {string.Join(", ", Enum.GetNames<BackendRole>())})");
		return false;
	}

	/// <summary>A 400 in the shape described on this class.</summary>
	internal static IResult BadRequest(string error)
	{
		return Results.BadRequest(new { error });
	}

	/// <summary>A 400 carrying per-input failures a form can mark, plus the summary message.</summary>
	internal static IResult BadRequest(string error, IEnumerable<BackendsEndpoints.FailureDto> failures)
	{
		return Results.BadRequest(new { error, failures });
	}

	/// <summary>
	///   Copies a per-account role's free-form settings for return to a client, masking any
	///   secret-named value the same way the global backends editor does (C5) — so an ApiKey/
	///   Token/ClientSecret on a role override never leaves the server in the clear. Returns null
	///   for an empty/absent map so the DTO shape is unchanged.
	/// </summary>
	internal static Dictionary<string, string?>? MaskSecretSettings(IReadOnlyDictionary<string, string?>? settings)
	{
		if (settings is not { Count: > 0 })
			return null;
		return settings.ToDictionary(
			pair => pair.Key,
			pair => SecretRedaction.MaskIfSecret(pair.Key, pair.Value),
			StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	///   Resolves the mask sentinel a save receives back against the stored settings: an incoming
	///   value equal to <see cref="SecretRedaction.Mask" /> means "unchanged", so it is replaced by
	///   the value already stored (and dropped when there is nothing behind the mask). Without this,
	///   masking on read (C5) would let a client that re-posts the form clobber a real secret with
	///   "***" — the same sentinel contract the global backends editor uses.
	/// </summary>
	internal static Dictionary<string, string?>? UnmaskSecretSettings(
		IReadOnlyDictionary<string, string?>? incoming, IReadOnlyDictionary<string, string?>? stored)
	{
		if (incoming is not { Count: > 0 })
			return null;
		Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, string? value) in incoming)
		{
			if (value == SecretRedaction.Mask)
			{
				if (stored is not null && stored.TryGetValue(key, out string? kept))
					result[key] = kept; // keep the real secret behind the mask
				continue; // a mask over nothing is a no-op, never stored literally
			}

			result[key] = value;
		}

		return result.Count > 0 ? result : null;
	}
}
