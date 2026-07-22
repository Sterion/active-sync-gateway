using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Everything a backend session needs for one gateway user: each role resolved to its
///   provider, effective settings and backend credentials. <see cref="GatewayLogin" /> is
///   THE identity: DB row scoping, change-notifier keys, encryption AAD and session/watcher
///   cache keys are all derived from it — per-backend user names never leak into those.
/// </summary>
public sealed record ResolvedAccount(
	string GatewayLogin,
	string? MailAddress,
	bool MailAddressIsExplicit,
	IReadOnlyDictionary<BackendRole, ResolvedRole> Roles)
{
	private IReadOnlyList<ResolvedRole>? _orderedRoles;

	/// <summary>Roles in a stable order (enum order, MailStore first) for session composition.</summary>
	// B28 (item 37): computed once and cached — the sort+allocation used to run on every read, and the
	// list identity changed each time, inviting O(n) LINQ inside a loop from an unwary caller.
	public IReadOnlyList<ResolvedRole> OrderedRoles =>
		_orderedRoles ??= Roles.Values.OrderBy(r => r.Role).ToList();
}
