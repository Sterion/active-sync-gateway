using ActiveSync.Core.Accounts;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Shape checks for the two identifiers the admin API stores without creating an account
///   behind them: the login a device block or a share grant names, and the collection href of a
///   grant. Neither is dereferenced at write time — the block is compared against the login a
///   phone presents and the href is resolved by the DAV server much later — so an unchecked
///   value does not fail, it persists as a row that can never match. Every other write path
///   already runs these rules; C16 was these two skipping them.
/// </summary>
internal static class AdminIdentifiers
{
	/// <summary>Why this login cannot be stored, or null when it is well-formed.</summary>
	internal static string? LoginProblem(string? login)
	{
		if (string.IsNullOrWhiteSpace(login))
			return "user is required";
		List<string> failures = [];
		AccountResolver.ValidateLogin(login.Trim(), failures);
		return failures.Count > 0
			? "user must not contain ':' or control characters"
			: null;
	}

	/// <summary>Why this collection href cannot be stored, or null when it is usable.</summary>
	internal static string? HrefProblem(string? href)
	{
		string value = href?.Trim() ?? "";
		if (!value.StartsWith('/'))
			return "the collection must be an absolute path starting with '/'";
		if (value.Any(char.IsControl))
			return "the collection must not contain control characters";
		// A ".." segment cannot address anything real — the DAV server normalizes the request
		// path before matching, so the grant it was meant to describe is not the one it names.
		if (value.Split('/').Any(segment => segment == ".."))
			return "the collection must not contain a '..' segment";
		return null;
	}
}
