using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>Effective endpoint settings plus the credentials to present to that backend.</summary>
public sealed record ResolvedBackend<TOptions>(TOptions Options, BackendCredentials Credentials);

/// <summary>
///   Everything a backend session needs for one gateway user. <see cref="GatewayLogin" /> is
///   THE identity: DB row scoping, change-notifier keys, encryption AAD and session/watcher
///   cache keys are all derived from it — per-backend user names never leak into those.
/// </summary>
public sealed record ResolvedAccount(
	string GatewayLogin,
	string? MailAddress,
	bool MailAddressIsExplicit,
	ResolvedBackend<ImapOptions> Imap,
	ResolvedBackend<SmtpOptions> Smtp,
	ResolvedBackend<DavServerOptions>? CalDav,
	ResolvedBackend<DavServerOptions>? CardDav,
	ResolvedBackend<SieveOptions>? Sieve = null);
