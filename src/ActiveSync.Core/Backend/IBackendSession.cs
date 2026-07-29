using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

// These are HOST-ONLY types — the composite backend session the host builds over an account's
// provider connections, its factory/cache, and the dashboard projection of that cache. A plugin
// never implements or receives them (it implements IBackendProvider / IBackendConnection / the
// store + side-op interfaces in ActiveSync.Contracts), so they do not belong on the published
// plugin surface. CompositeBackendSession and BackendSessionFactory (same namespace) implement them.

/// <summary>
///   A per-user backend session bundling the content stores and side operations. Sessions cache
///   live protocol connections (IMAP) and are reused across requests for the same user+device.
/// </summary>
public interface IBackendSession : IAsyncDisposable
{
	/// <summary>The gateway credentials — the presented login, never a backend login.</summary>
	BackendCredentials Credentials { get; }

	/// <summary>The immutable gateway user id — THE identity (DB scoping, encryption AAD).</summary>
	int UserId { get; }

	/// <summary>
	///   The user's mail address (explicit in Accounts mode; in PassThrough the login when it
	///   contains '@'). Null when unknown — consumers must degrade, not guess.
	/// </summary>
	string? MailAddress { get; }

	/// <summary>All content stores available for this deployment (mail always; DAV stores if configured).</summary>
	IReadOnlyList<IContentStore> Stores { get; }

	/// <summary>The mail store's item surface (always present — MailStore is a mandatory role).</summary>
	IMailStore Mail { get; }

	/// <summary>The mailbox side operations (search, raw fetch, Sent filing, empty-folder).</summary>
	IMailboxOperations Mailbox { get; }

	/// <summary>Outbound submission (always present — MailSubmit is a mandatory role).</summary>
	IMailSubmitOperations MailSubmit { get; }

	/// <summary>GAL search; null when the contact store offers none.</summary>
	IDirectoryOperations? Contacts { get; }

	/// <summary>Meeting/scheduling operations; null when the calendar store offers none.</summary>
	IMeetingOperations? Calendar { get; }

	/// <summary>Sieve-backed out-of-office management; null when Sieve is not configured.</summary>
	IOofBackend? Oof { get; }

	/// <summary>
	///   The session's revision-keyed payload cache: what this (user, device) was last sent, so a
	///   partial-update merge usually needs no backend fetch. See <see cref="SessionPayloadCache" />.
	/// </summary>
	SessionPayloadCache PayloadCache { get; }

	/// <summary>The store serving an EAS content class, or null when the class has none.</summary>
	/// <param name="easClass">The EAS class string ("Email", "Calendar", …).</param>
	IContentStore? GetStoreForClass(string easClass);

	/// <summary>The store owning a folder key, or null when no store claims it.</summary>
	/// <param name="key">The folder key to dispatch on.</param>
	IContentStore? GetStoreForKey(FolderKey key);

	/// <summary>The EAS content class a store of this session serves (derived from its alias interface).</summary>
	/// <param name="store">One of this session's stores.</param>
	string EasClassOf(IContentStore store);

	/// <summary>
	///   Whether the folder is granted read-only (shared calendars): client writes are then
	///   silently reverted, the same convergence semantics as global ReadOnly mode.
	/// </summary>
	/// <param name="folder">The folder to test.</param>
	bool IsReadOnlyFolder(FolderKey folder);
}

/// <summary>The host's session cache: authenticates logins and builds/caches composite sessions.</summary>
public interface IBackendSessionFactory
{
	/// <summary>Validates credentials against the mail backend (used by HTTP Basic auth).</summary>
	Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct);

	/// <summary>Gets or creates a cached session for the user/device pair.</summary>
	Task<IBackendSession> GetSessionAsync(
		BackendCredentials credentials, int userId, string deviceId, CancellationToken ct);
}

/// <summary>One live backend session of the factory cache (for the admin dashboard).</summary>
public sealed record BackendSessionInfo(string User, string DeviceId, DateTime LastUsedUtc);
