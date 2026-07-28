// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace ActiveSync.Contracts;

/// <summary>
///   The functional slots a backend session must fill. Config assigns each role to a named
///   provider; one provider may serve several roles over a single connection (IMAP serves
///   MailStore, CalDAV serves Calendar+Tasks, a future JMAP provider could serve five).
/// </summary>
public enum BackendRole
{
	/// <summary>Mail retrieval/storage (IMAP-shaped: folders, messages, flags). Mandatory — every account needs one.</summary>
	MailStore,

	/// <summary>Outbound mail submission (SMTP-shaped send). Mandatory — every account needs one.</summary>
	MailSubmit,

	/// <summary>Calendar events. Falls back to the local store when unconfigured.</summary>
	Calendar,

	/// <summary>Tasks. Falls back to the local store when unconfigured.</summary>
	Tasks,

	/// <summary>Contacts. Falls back to the local store when unconfigured.</summary>
	Contacts,

	/// <summary>Notes. Always served by the local store — no in-repo backend implements it.</summary>
	Notes,

	/// <summary>Out-of-office (vacation) responder. No configured provider = accept-and-ignore stub.</summary>
	Oof
}

/// <summary>
///   Effective settings of one role for one account (the global role section, overlaid with
///   the user's per-role settings). The host never binds these — the provider that owns the
///   role binds its OWN options type, which is what lets plugins carry option shapes the
///   host cannot know. The section never contains credentials; those travel separately as
///   <see cref="ResolvedRole.Credentials" />.
/// </summary>
public sealed class ProviderSettings(IConfigurationSection section)
{
	private static readonly IConfigurationSection EmptySection =
		new ConfigurationBuilder().Build().GetSection("empty");

	/// <summary>Settings with no keys at all (the "local" provider, absent sections).</summary>
	public static ProviderSettings Empty { get; } = new(EmptySection);

	/// <summary>The raw configuration section this instance wraps. Never contains credentials.</summary>
	public IConfigurationSection Section => section;

	/// <summary>Binds the section onto a fresh instance of the provider's options type.</summary>
	public TOptions Bind<TOptions>() where TOptions : new()
	{
		TOptions options = new();
		section.Bind(options);
		return options;
	}

	/// <summary>
	///   Materializes flat leaf keys ("Host", "SharedCollections:0") into a section, so a set of
	///   entered/stored values can be handed to a provider for binding or validation without a
	///   file behind it. Null values are dropped (an absent key, not an empty one).
	/// </summary>
	public static ProviderSettings FromFlat(IReadOnlyDictionary<string, string?> flat)
	{
		if (flat.Count == 0)
			return Empty;
		IConfigurationRoot materialized = new ConfigurationBuilder()
			.AddInMemoryCollection(flat
				.Where(pair => pair.Value is not null)
				.ToDictionary(pair => "S:" + pair.Key, string? (pair) => pair.Value))
			.Build();
		return new ProviderSettings(materialized.GetSection("S"));
	}
}

/// <summary>
///   One role resolved for one account: which provider serves it, with what settings and
///   backend credentials.
/// </summary>
public sealed record ResolvedRole(
	BackendRole Role, string ProviderName, ProviderSettings Settings, BackendCredentials Credentials);

/// <summary>
///   Everything a provider needs to open one account's connection: the gateway identity
///   (<see cref="GatewayUserId" /> — the immutable per-user id used for DB scoping, the
///   encryption AAD and durable keys; <see cref="GatewayCredentials" /> carries the login the
///   phone presented — never a backend login), the roles assigned to THIS provider, and the
///   shared-calendar grants. Host services (db factory, change notifier, logging) reach
///   providers through normal constructor injection instead.
/// </summary>
public sealed record BackendConnectionContext(
	BackendCredentials GatewayCredentials,
	int GatewayUserId,
	string? MailAddress,
	IReadOnlyList<ResolvedRole> Roles,
	IReadOnlyList<SharedCollection> SharedCollections);

/// <summary>One provider's connection bundle for one account: its stores and side operations.</summary>
public interface IBackendConnection : IAsyncDisposable
{
	/// <summary>One content store per content role this connection fills.</summary>
	IReadOnlyList<IContentStore> Stores { get; }

	/// <summary>Set when the connection fills the MailSubmit role.</summary>
	IMailSubmitOperations? MailSubmit { get; }

	/// <summary>Set when the connection fills the Oof role.</summary>
	IOofBackend? Oof { get; }
}

/// <summary>Ready-made <see cref="IBackendConnection" /> that disposes its owned resources.</summary>
/// <remarks>
///   Disposal is idempotent (a second call is a no-op), keeps going when one resource throws
///   (surfacing the failures as an <see cref="AggregateException" /> so no later resource leaks a
///   live socket), and also disposes any content store that is itself disposable — stores routinely
///   hold connections, and the provider is no longer required to remember to list them in
///   <paramref name="ownedResources" />. The parameter stays <c>object</c>-typed because the owned
///   resources are a mix of <see cref="IAsyncDisposable" /> (ImapSession) and <see cref="IDisposable" />
///   (WebDavClient, JmapClient), which no single disposable interface covers.
///   The idempotence guard is an <c>int</c> flipped with <see cref="Interlocked.Exchange(ref int, int)" />,
///   not a plain <c>bool</c> read-then-write — two callers racing DisposeAsync (a session-eviction
///   sweep vs. a request completing, both plausible in <c>BackendSessionFactory</c>) could otherwise
///   both observe "not yet disposed" and both dispose the same owned resource.
/// </remarks>
public sealed class BackendConnection(
	IReadOnlyList<IContentStore> stores,
	IMailSubmitOperations? mailSubmit = null,
	IOofBackend? oof = null,
	IReadOnlyList<object>? ownedResources = null) : IBackendConnection
{
	private int _disposed;

	/// <inheritdoc />
	public IReadOnlyList<IContentStore> Stores => stores;

	/// <inheritdoc />
	public IMailSubmitOperations? MailSubmit => mailSubmit;

	/// <inheritdoc />
	public IOofBackend? Oof => oof;

	/// <summary>
	///   Disposes every owned resource and disposable store exactly once. See the type-level
	///   remarks for ordering, idempotence and failure-aggregation guarantees.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		// Interlocked.Exchange makes the check-and-set a single atomic operation — only the
		// caller that flips 0 -> 1 proceeds, however many race in concurrently.
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		List<Exception>? failures = null;

		// Build the owned-resource identity set ONCE (reference equality) instead of an
		// O(stores × ownedResources) Any(ReferenceEquals) scan inside the store loop below.
		HashSet<object> owned = ownedResources is { Count: > 0 }
			? new HashSet<object>(ownedResources, ReferenceEqualityComparer.Instance)
			: [];

		// Dispose every owned resource, plus any store that owns a connection — never let one
		// throwing disposal strand the rest (a live IMAP/HTTP socket leaks otherwise).
		foreach (object resource in ownedResources ?? [])
			await SafeDisposeAsync(resource).ConfigureAwait(false);
		foreach (IContentStore store in stores)
			// A store explicitly listed as an owned resource is disposed once, above.
			if (store is not IAsyncDisposable and not IDisposable || owned.Contains(store))
				continue;
			else
				await SafeDisposeAsync(store).ConfigureAwait(false);

		if (failures is { Count: > 0 })
			throw new AggregateException("One or more backend resources failed to dispose.", failures);

		async ValueTask SafeDisposeAsync(object resource)
		{
			try
			{
				switch (resource)
				{
					case IAsyncDisposable asyncDisposable:
						await asyncDisposable.DisposeAsync().ConfigureAwait(false);
						break;
					case IDisposable disposable:
						disposable.Dispose();
						break;
				}
			}
			catch (Exception ex)
			{
				(failures ??= []).Add(ex);
			}
		}
	}
}

/// <summary>
///   A named backend implementation ("imap", "caldav", "local", later "jmap") that can fill
///   one or more roles. Providers are process singletons — per-user caches (auth, watchers)
///   belong to the provider, per-account state belongs to the connections it creates.
/// </summary>
public interface IBackendProvider
{
	/// <summary>Unique name config refers to; compared case-insensitively.</summary>
	string Name { get; }

	/// <summary>
	///   Roles this provider is capable of serving. Config may assign any subset of these to it —
	///   a provider serves every role assigned to it over the ONE connection
	///   <see cref="CreateConnectionAsync" /> returns (the JMAP shape: one provider can fill
	///   MailStore + MailSubmit + Calendar + Contacts + Oof over a single session).
	/// </summary>
	IReadOnlySet<BackendRole> SupportedRoles { get; }

	/// <summary>
	///   One connection serving ALL roles assigned to this provider for one account.
	///   Async — a provider opens its transport here (a TCP/TLS connect, an auth round-trip),
	///   and the rest of the contract is async end-to-end. Landed as the async shape while the
	///   plugin ecosystem was still empty, so no out-of-repo provider had to be rewritten later.
	/// </summary>
	Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct);

	/// <summary>
	///   Validates one effective role section (the provider binds its own options). Called for
	///   the global sections at startup (strict — failures abort) and for per-user overrides
	///   (config entries strict, database entries skip-with-warning).
	/// </summary>
	void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures);

	/// <summary>One redacted human-readable line for the startup banner (never secrets).</summary>
	string DescribeRole(BackendRole role, ProviderSettings settings);

	/// <summary>
	///   The settings this provider reads for the role, described so a UI can render a form for
	///   them without knowing the option type. The default is empty — a provider (in particular
	///   an out-of-repo plugin built against an older contract) that describes nothing simply
	///   falls back to the raw key/value editors. Credentials are NOT fields: UserName/Password
	///   are host-reserved per-user override keys, rendered separately.
	/// </summary>
	IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role) => [];
}

/// <summary>
///   Optional provider capability: verifies presented credentials against the backend (the
///   HTTP Basic auth path when no local rule decides). Implementations return false on bad
///   credentials and throw <see cref="BackendException" /> when the backend is unreachable.
/// </summary>
public interface ICredentialVerifier
{
	/// <summary>
	///   Verifies the credentials carried by <paramref name="role" /> against the live backend
	///   (a login probe — e.g. an IMAP LOGIN). Returns <c>false</c> for a rejected credential;
	///   throws <see cref="BackendException" /> when the backend itself could not be reached
	///   (a distinction the caller relies on to tell "wrong password" from "backend is down").
	/// </summary>
	/// <param name="role">The resolved role (provider, settings and credentials) to probe.</param>
	/// <param name="ct">Cancellation token for the probe's I/O.</param>
	/// <returns><c>true</c> if the credentials are accepted by the backend; otherwise <c>false</c>.</returns>
	Task<bool> VerifyCredentialsAsync(ResolvedRole role, CancellationToken ct);
}

/// <summary>
///   Optional provider capability: providers holding per-user caches (e.g. shared IMAP IDLE
///   watchers) trim them when the session factory's eviction sweep finds users without live
///   sessions. Called every sweep with the currently active gateway logins.
/// </summary>
public interface IPerUserResourceOwner
{
	/// <summary>Synchronous by design — disposal of trimmed resources happens in the background.</summary>
	void TrimUserResources(IReadOnlySet<string> activeGatewayLogins);
}

// BackendSessionInfo (a projection of the HOST's session cache for the admin dashboard) moved
// to ActiveSync.Core.Backend with IBackendSessionFactory that produces it. WatcherInfo stays: it is
// the return type of IWatcherDiagnostics, an OPTIONAL PROVIDER capability a plugin may implement.

/// <summary>One live push watcher a provider holds (for the admin dashboard).</summary>
public sealed record WatcherInfo(string User, string Resource);

/// <summary>
///   Optional provider capability: live watcher state for the admin dashboard (e.g. the
///   shared IMAP IDLE watchers). Purely observational — never mutates provider state.
/// </summary>
public interface IWatcherDiagnostics
{
	/// <summary>
	///   Point-in-time snapshot of this provider's live push watchers, for the admin dashboard.
	///   Must not mutate provider state; called synchronously and frequently, so it should be cheap.
	/// </summary>
	/// <returns>The currently live watchers, empty when none are active.</returns>
	IReadOnlyList<WatcherInfo> SnapshotWatchers();
}

/// <summary>
///   Optional provider capability: a cheap reachability probe of the globally configured
///   endpoint for /readyz (no credentials — connectivity only, e.g. TCP banner or HTTP
///   OPTIONS). Providers without it simply do not appear in the readiness report.
/// </summary>
public interface IReadinessSource
{
	/// <summary>
	///   Cheap reachability probe of the globally configured endpoint (no credentials — connectivity
	///   only, e.g. a TCP connect or an HTTP OPTIONS) for the <c>/readyz</c> report.
	/// </summary>
	/// <param name="settings">The role's globally configured settings (never per-user overrides).</param>
	/// <param name="ct">Cancellation token for the probe's I/O.</param>
	/// <returns><c>true</c> when the endpoint answered; <c>false</c> otherwise. Must not throw.</returns>
	Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct);
}
