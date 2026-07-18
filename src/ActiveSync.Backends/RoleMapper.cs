using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Backends;

/// <summary>
///   Transitional shim: maps the typed <see cref="ResolvedAccount" /> slots onto role→provider
///   assignments using the historical presence rules (a configured DAV side wins, the gateway
///   database is the fallback). Dissolves when config gains explicit role sections with
///   provider discriminators and the resolver produces roles directly.
/// </summary>
internal static class RoleMapper
{
	public static IReadOnlyList<ResolvedRole> Map(ResolvedAccount account, BackendCredentials gatewayCredentials)
	{
		bool calDavTasks = account.CalDav is not null &&
		                   !string.IsNullOrWhiteSpace(account.CalDav.Options.TaskFolder);
		List<ResolvedRole> roles =
		[
			new ResolvedRole(BackendRole.MailStore, "imap", account.Imap.Options, account.Imap.Credentials),
			new ResolvedRole(BackendRole.MailSubmit, "smtp", account.Smtp.Options, account.Smtp.Credentials),
			account.CalDav is not null
				? new ResolvedRole(BackendRole.Calendar, "caldav", account.CalDav.Options, account.CalDav.Credentials)
				: new ResolvedRole(BackendRole.Calendar, "local", null, gatewayCredentials),
			calDavTasks
				? new ResolvedRole(BackendRole.Tasks, "caldav", account.CalDav!.Options, account.CalDav.Credentials)
				: new ResolvedRole(BackendRole.Tasks, "local", null, gatewayCredentials),
			account.CardDav is not null
				? new ResolvedRole(BackendRole.Contacts, "carddav", account.CardDav.Options, account.CardDav.Credentials)
				: new ResolvedRole(BackendRole.Contacts, "local", null, gatewayCredentials),
			new ResolvedRole(BackendRole.Notes, "local", null, gatewayCredentials)
		];
		if (account.Sieve is not null)
			roles.Add(new ResolvedRole(BackendRole.Oof, "sieve", account.Sieve.Options, account.Sieve.Credentials));
		return roles;
	}
}
