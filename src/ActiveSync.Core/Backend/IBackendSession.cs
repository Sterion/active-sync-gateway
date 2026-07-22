using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

// K57: these are HOST-ONLY types — the composite backend session the host builds over an account's
// provider connections, its factory/cache, and the dashboard projection of that cache. A plugin
// never implements or receives them (it implements IBackendProvider / IBackendConnection / the
// store + side-op interfaces in ActiveSync.Contracts), so they do not belong on the published
// plugin surface. They were moved here from ActiveSync.Contracts. CompositeBackendSession and
// BackendSessionFactory (same namespace) implement them.

/// <summary>
///   A per-user backend session bundling the content stores and side operations. Sessions cache
///   live protocol connections (IMAP) and are reused across requests for the same user+device.
/// </summary>
public interface IBackendSession : IAsyncDisposable
{
	/// <summary>The gateway credentials — the user's identity, not any backend login.</summary>
	BackendCredentials Credentials { get; }

	/// <summary>
	///   The user's mail address (explicit in Accounts mode; in PassThrough the login when it
	///   contains '@'). Null when unknown — consumers must degrade, not guess.
	/// </summary>
	string? MailAddress { get; }

	/// <summary>All content stores available for this deployment (mail always; DAV stores if configured).</summary>
	IReadOnlyList<IContentStore> Stores { get; }

	IMailStoreOperations MailStore { get; }
	IMailSubmitOperations MailSubmit { get; }
	IContactOperations? Contacts { get; }
	ICalendarOperations? Calendar { get; }

	/// <summary>Sieve-backed out-of-office management; null when Sieve is not configured.</summary>
	IOofBackend? Oof { get; }

	IContentStore? GetStoreForClass(string easClass);
	IContentStore? GetStoreForBackendKey(string backendKey);

	/// <summary>
	///   Whether the folder is granted read-only (shared calendars): client writes are then
	///   silently reverted, the same convergence semantics as global ReadOnly mode.
	/// </summary>
	bool IsReadOnlyFolder(string folderBackendKey);
}

public interface IBackendSessionFactory
{
	/// <summary>Validates credentials against the mail backend (used by HTTP Basic auth).</summary>
	Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct);

	/// <summary>Gets or creates a cached session for the user/device pair.</summary>
	Task<IBackendSession> GetSessionAsync(BackendCredentials credentials, string deviceId, CancellationToken ct);
}

/// <summary>One live backend session of the factory cache (for the admin dashboard).</summary>
public sealed record BackendSessionInfo(string User, string DeviceId, DateTime LastUsedUtc);
