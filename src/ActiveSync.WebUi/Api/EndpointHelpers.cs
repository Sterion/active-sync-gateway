using ActiveSync.Contracts;
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
}
