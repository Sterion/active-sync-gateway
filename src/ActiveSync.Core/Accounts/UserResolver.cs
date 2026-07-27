using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   One entry of the merged user view, for banners, the CLI and the admin API.
///   <paramref name="Options" /> is the PER-FIELD resolution of the database declaration over the
///   configuration one (<see cref="UserMerge" />) — not one replacing the other — and
///   <paramref name="Sources" /> records which level supplied each field, keyed by the same paths
///   the CLI and admin API address fields with ("MailAddress", "Backends:MailStore:UserName", …).
///   <paramref name="FromDatabase" /> means a database declaration contributed;
///   <paramref name="ShadowsConfig" /> means both levels did.
///   <paramref name="Invalid" /> marks a merge that failed validation: it is kept in the view
///   (so operators see it, and <see cref="UserResolver.IsLoginDisabled" /> still honours its
///   <see cref="UserOptions.Enabled" />) but the login is refused (fail-closed) until corrected.
/// </summary>
public sealed record MergedUser(
	UserOptions Options,
	bool FromDatabase,
	bool ShadowsConfig,
	bool Invalid = false,
	IReadOnlyDictionary<string, UserFieldSource>? Sources = null)
{
	/// <summary>Which level supplied this field path, or null when nothing set it.</summary>
	public UserFieldSource? SourceOf(string fieldPath) =>
		Sources is not null && Sources.TryGetValue(fieldPath, out UserFieldSource source) ? source : null;
}

/// <summary>
///   Maps a gateway login to its effective backend roles and credentials. Pass-through is the
///   baseline: undeclared logins use the global role sections with the presented credentials
///   everywhere. A declaration is a pure per-field OVERLAY — only the fields it sets differ.
///   <para>
///     Entries come from configuration (<see cref="ActiveSyncOptions.Users" />, restart to
///     change) and from the database (<see cref="UserStore" />, picked up live), and the two are
///     merged PER FIELD by <see cref="UserMerge" /> — database over configuration — rather than
///     one replacing the other. Credentials then resolve with one extra tier of scope:
///     <c>user · role → user · default → pass-through</c>, where the terminal step forwards the
///     presented EAS credential and the gateway login, which is what keeps the
///     zero-administration baseline working.
///   </para>
///   <para>
///     The compiled snapshot is immutable and swapped atomically; database changes are noticed
///     via the <c>"users"</c> <see cref="State.DataChange" /> point-read at most every
///     <see cref="AuthOptions.UsersRefreshSeconds" />. Registered as a singleton.
///   </para>
/// </summary>
public sealed class UserResolver
{
	private readonly IOptionsMonitor<ActiveSyncOptions> _options;
	private readonly BackendRolesProvider _rolesProvider;
	private readonly BackendProviderRegistry _registry;
	private readonly UserStore? _store;
	private readonly ILogger<UserResolver>? _logger;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly Settings.ChangeStampRefreshGate _gate = new();
	// B4: serializes the BUILD-AND-SWAP of _snapshot between the two independent writers —
	// EnsureFreshAsync (account refresh, request path) and OnRolesChanged (a live "Backends" edit,
	// config-reload thread). _refreshGate only keeps concurrent EnsureFreshAsync calls from
	// overlapping each other; it says nothing about OnRolesChanged, which used to swap with no
	// coordination at all. Building INSIDE this lock (not just the final assignment) matters: it
	// means whichever writer runs SECOND observes the fully-applied state the first one just
	// installed (current roles, current database users) rather than racing a build that started
	// against now-superseded inputs — so neither writer can ever clobber the other's newer snapshot
	// with one computed from stale roles.
	private readonly object _snapshotSwapLock = new();
	private volatile Snapshot _snapshot;
	// A8: monotonic — bumped every time _snapshot is swapped (both rebuild paths below), NEVER on
	// the constructor's initial build. BackendSessionFactory stamps each cached auth verdict with
	// the version live when it was computed and rejects a hit whose stamp doesn't match the
	// CURRENT version, closing the TOCTOU where SnapshotChanged clears the caches but an
	// in-flight verdict (computed against the old snapshot) writes back afterward — that stale
	// write is still tagged with the old version, so the next read treats it as a miss instead of
	// trusting it.
	private long _snapshotVersion = 1;
	// _lastStamp is a Guid? (cannot be `volatile`) but is only ever touched inside _refreshGate,
	// whose SemaphoreSlim Wait/Release are full memory barriers. _lastDbUsers is the one field read
	// OUTSIDE that gate — OnRolesChanged runs on the config-reload thread — so it must be volatile to
	// avoid compiling a rebuild against a stale reference (B23).
	private Guid? _lastStamp;
	private volatile Dictionary<string, UserOptions>? _lastDbUsers;
	private volatile bool _refreshErrorLogged;

	/// <summary>Raised after the snapshot was rebuilt from a database change (caches should reset).</summary>
	public event Action? SnapshotChanged;

	public UserResolver(
		IOptionsMonitor<ActiveSyncOptions> options,
		BackendRolesProvider rolesProvider,
		BackendProviderRegistry registry,
		UserStore? store = null,
		ILogger<UserResolver>? logger = null)
	{
		_options = options;
		_rolesProvider = rolesProvider;
		_registry = registry;
		_store = store;
		_logger = logger;
		// Config-only snapshot first; database entries arrive with the first EnsureFreshAsync
		// (the server forces one right after migrations, before any request).
		_snapshot = BuildSnapshot(_options.CurrentValue, _rolesProvider.Current, _registry, null, logger);
		// A live backend-settings change (eas config set Backends:...) rebuilds the snapshot so
		// declared users pick up the new global role settings; pass-through reads Current directly.
		_rolesProvider.Changed += OnRolesChanged;
	}

	/// <summary>The global role assignments (for banners, readiness probes and the CLI).</summary>
	public BackendRolesConfig Roles => _rolesProvider.Current;

	/// <summary>
	///   Monotonic snapshot generation, bumped on every rebuild (A8). Callers that cache a
	///   verdict computed against the current snapshot should stamp it with this value and
	///   distrust a cached hit whose stamp no longer matches.
	/// </summary>
	public long SnapshotVersion => Interlocked.Read(ref _snapshotVersion);

	/// <summary>The merged, effective user view (database over config, per field).</summary>
	public IReadOnlyDictionary<string, MergedUser> MergedUsers => _snapshot.Users;

	/// <summary>
	///   True when <paramref name="login" /> is a declared account explicitly disabled
	///   (<see cref="UserOptions.Enabled" /> == false) — a persistent refusal of every login,
	///   enforced at the endpoint like a user-level block. Reads the current in-memory snapshot, so
	///   it is cheap on the auth path; an undeclared/pass-through login has no row and is never
	///   "disabled" (block it instead). Case-insensitive, matching config/database key semantics.
	/// </summary>
	public bool IsLoginDisabled(string login) =>
		_snapshot.Users.TryGetValue(login, out MergedUser? account) && account.Options.Enabled == false;

	/// <summary>
	///   Reloads database accounts when the change stamp moved. Cost when idle: one
	///   primary-key point-read at most every UsersRefreshSeconds, on the calling request.
	///   Failures keep the current snapshot (auth never goes down with the database).
	/// </summary>
	public async Task EnsureFreshAsync(bool force, CancellationToken ct)
	{
		if (_store is null)
			return;
		double refreshSeconds = _options.CurrentValue.Auth.UsersRefreshSeconds;
		if (!_gate.ShouldCheck(force))
			return;

		// A refresh already in flight serves this caller fine — use the current snapshot.
		if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
			return;
		try
		{
			Guid? stamp = await _store.ReadStampAsync(ct).ConfigureAwait(false);
			if (stamp != _lastStamp)
			{
				Dictionary<string, UserOptions>? dbUsers = stamp is null
					? null
					: await _store.LoadAllAsync(_logger, ct).ConfigureAwait(false);
				Snapshot built;
				// B4: build AND swap under the same lock OnRolesChanged uses. A role change landing
				// on the config-reload thread while THIS build is still running (captured against
				// the roles in force when it started) must not have its own, newer snapshot clobbered
				// by this one finishing last with stale role settings baked in.
				lock (_snapshotSwapLock)
				{
					_lastDbUsers = dbUsers;
					built = BuildSnapshot(_options.CurrentValue, _rolesProvider.Current, _registry, dbUsers, _logger);
					_snapshot = built;
					_lastStamp = stamp;
					Interlocked.Increment(ref _snapshotVersion);
				}

				_logger?.LogInformation(
					"Accounts snapshot rebuilt: {Count} declared user(s) ({Db} from database)",
					built.Users.Count, built.Users.Count(u => u.Value.FromDatabase));
				SnapshotChanged?.Invoke();
			}

			_refreshErrorLogged = false;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (!_refreshErrorLogged)
			{
				_logger?.LogWarning(ex, "Could not refresh database accounts; keeping the current snapshot");
				_refreshErrorLogged = true;
			}
		}
		finally
		{
			_gate.ScheduleNext(refreshSeconds);
			_refreshGate.Release();
		}
	}

	/// <summary>
	///   Rebuilds the snapshot when the global backend role configuration changed (a live
	///   settings edit), so declared users inherit the new role settings. Uses the last-loaded
	///   database users; pass-through resolution already reads the current roles directly.
	///   B4: builds AND swaps under the same lock <see cref="EnsureFreshAsync" /> uses — see
	///   <see cref="_snapshotSwapLock" /> for why a bare atomic swap is not enough.
	/// </summary>
	private void OnRolesChanged()
	{
		Snapshot rebuilt;
		lock (_snapshotSwapLock)
		{
			try
			{
				rebuilt = BuildSnapshot(
					_options.CurrentValue, _rolesProvider.Current, _registry, _lastDbUsers, _logger);
			}
			catch (Exception ex)
			{
				// B6: config entries are treated as strict (startup already validated them), but a LIVE
				// backend edit can newly invalidate a config user's merge — and BuildSnapshot then throws.
				// Left uncaught the throw escaped through this Changed handler and out of the settings
				// reload that fired it, mislogged as a settings-refresh failure, and left the snapshot
				// stale forever (the roles provider had already committed the new signature, so it never
				// fired again). Keep the previous (last-good) snapshot and log against the resolver.
				_logger?.LogWarning(ex,
					"Backend configuration change left one or more declared users invalid; " +
					"keeping the previous account snapshot until the configuration is corrected");
				return;
			}

			_snapshot = rebuilt;
			Interlocked.Increment(ref _snapshotVersion);
		}

		SnapshotChanged?.Invoke();
	}

	/// <summary>
	///   Local gateway-password verdict, or null when only a backend login probe can decide.
	///   Precedence: explicit gateway Password (hash/plaintext) → null (probe). Undeclared logins:
	///   definitive false when <see cref="ActiveSyncOptions.AutoProvisionUsers" /> is OFF — the
	///   refusal lands here, BEFORE any backend probe, so undeclared logins never reach the mail
	///   server (a brute-force shield, not just policy); else null.
	///   <para>
	///     A BACKEND credential is never consulted here, by design: the two chains are separate
	///     trust domains and a GATEWAY → BACKENDS secret must never become a DEVICE → GATEWAY one.
	///     The reason this is safe — that the probe can only ever be reached when it sends the
	///     PRESENTED password — is enforced upstream at validation; see
	///     <see cref="RequireGatewayPasswordForStoredMailSecret" /> for why the two are the same rule.
	///   </para>
	/// </summary>
	public bool? VerifyLocally(string login, string presented)
	{
		UserTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
			return _options.CurrentValue.AutoProvisionUsers ? null : false;
		// B3: an invalid stored row fails closed — it never authenticates and never falls through to
		// the backend probe (which would admit it as pass-through with the presented credentials).
		// This is also what catches a user whose stored MailStore secret has no gateway password:
		// the combination is refused at validation, so it lands here as a definitive false rather
		// than reaching the probe.
		if (template.Invalid)
			return false;
		if (template.GatewayPassword is not null)
			return GatewayPasswordHasher.Verify(template.GatewayPassword, presented);
		return null;
	}

	/// <summary>Effective account for the presented credentials; never null.</summary>
	public ResolvedUser Resolve(BackendCredentials presented)
	{
		string login = presented.UserName;
		UserTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
		{
			// Pass-through: same credentials everywhere, the global role sections verbatim.
			Dictionary<BackendRole, ResolvedRole> passThrough = new();
			foreach ((BackendRole role, RoleAssignment assignment) in _rolesProvider.Current.Assignments)
				passThrough[role] = new ResolvedRole(role, assignment.ProviderName, assignment.Settings, presented);
			return new ResolvedUser(
				login, login.Contains('@') ? login : null, false, passThrough);
		}

		// B3: an invalid stored row refuses resolution rather than degrading to pass-through. In
		// practice VerifyLocally already refused the login (false) before any session is built, so
		// this is defence in depth against a caller that resolves without authenticating first.
		if (template.Invalid)
			throw new InvalidOperationException(
				$"Account '{login}' has an invalid stored configuration and cannot be resolved; " +
				"correct or remove the database row (the login is refused until then).");

		// THE RESOLUTION RULE with one extra tier of SCOPE (this role vs every role) — the DB/config
		// half of each tier was already collapsed per field by UserMerge, so what is left is:
		//
		//   user · role  →  user · default  →  pass-through
		//
		// The terminal step is what keeps zero-administration working: an unset default login is
		// the gateway login and an unset default password is the PRESENTED EAS credential, so a
		// user with nothing declared behaves exactly as pass-through always did.
		//
		// MailStore is just another role here. It used to do double duty — "the mail backend" AND
		// "the template every other role copies from" — which only ever worked while the device
		// credential WAS the mail password; the explicit defaults replace that implicit chain.
		string defaultUser = template.DefaultBackendLogin ?? login;
		string defaultPassword = template.DefaultBackendPassword ?? presented.Password;
		Dictionary<BackendRole, ResolvedRole> roles = new();
		foreach ((BackendRole role, RoleTemplate roleTemplate) in template.Roles)
			roles[role] = new ResolvedRole(role, roleTemplate.ProviderName, roleTemplate.Settings,
				new BackendCredentials(
					roleTemplate.UserName ?? defaultUser,
					roleTemplate.Password ?? defaultPassword));
		return new ResolvedUser(
			login,
			template.MailAddress ?? (login.Contains('@') ? login : null),
			template.MailAddress is not null,
			roles);
	}

	/// <summary>Validation entry point — same merge/unseal code the runtime templates use.</summary>
	public static void ValidateUsers(
		ActiveSyncOptions options, BackendRolesConfig roles, BackendProviderRegistry registry,
		byte[]? encryptionKey, List<string> failures)
	{
		if (options.Users is null)
			return;
		ValidationMemo memo = new();
		foreach ((string login, UserOptions account) in options.Users)
		{
			ValidateLogin(login, failures);
			BuildOne(roles, registry, login, account, encryptionKey, failures, memo);
		}
	}

	/// <summary>
	///   Validates one would-be entry (CLI/web writes) against the global role sections —
	///   identical rules to config entries. Returns the failure messages; empty = valid.
	///   <para>
	///     The row is judged AS MERGED over any configuration entry for the same login, because
	///     that merge is what will actually take effect (<see cref="BuildSnapshot" /> validates the
	///     same shape). Cross-field rules make the difference load-bearing rather than cosmetic:
	///     <see cref="RequireGatewayPasswordForStoredMailSecret" /> reads a gateway Password and a
	///     stored MailStore secret that may arrive from DIFFERENT levels, so judging the row alone
	///     would refuse a legitimate write whose partner field lives in config — and, worse, would
	///     pass a write that completes a refused combination already half-declared there. It also
	///     gives the removal half its meaning: scalar fields cannot be cleared by a database null
	///     (<see cref="UserMerge" />), so "unset the gateway Password" means "fall back to the
	///     config one", and only the merge knows whether one exists.
	///   </para>
	/// </summary>
	public static List<string> ValidateEntry(
		ActiveSyncOptions options, BackendRolesConfig roles, BackendProviderRegistry registry,
		string login, UserOptions entry)
	{
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
		List<string> failures = new();
		if (keyError is not null)
			failures.Add(keyError);
		ValidateLogin(login, failures);
		// B8: the case-insensitive lookup — ActiveSyncOptions.Users binds with the ordinal comparer
		// while logins are case-insensitive everywhere else, so an indexer miss here would validate
		// a differently-cased edit against no config at all.
		UserOptions effective = UserMerge.Merge(UserEditing.FindConfigUser(options, login), entry).Options;
		BuildOne(roles, registry, login, effective, key, failures, new ValidationMemo());
		if (key is not null)
			CryptographicOperations.ZeroMemory(key);
		return failures;
	}

	/// <summary>
	///   Compiles the immutable snapshot, resolving each user PER FIELD across the two user
	///   levels — database over configuration (<see cref="UserMerge" />) — before compiling the
	///   result against the global role sections. A config-only user is strict (invalid config
	///   already failed startup validation; direct construction throws); anything a database
	///   declaration contributed is lenient — an invalid merge is kept visible but refused, so a
	///   bad row written by an older/newer CLI can never take authentication down.
	/// </summary>
	private static Snapshot BuildSnapshot(
		ActiveSyncOptions options, BackendRolesConfig roles, BackendProviderRegistry registry,
		Dictionary<string, UserOptions>? dbUsers, ILogger? logger)
	{
		Dictionary<string, UserTemplate> templates = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, MergedUser> merged = new(StringComparer.OrdinalIgnoreCase);
		bool needKey = options.Users is { Count: > 0 } || dbUsers is { Count: > 0 };
		byte[]? key = null;
		if (needKey)
		{
			key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
			if (key is null && keyError is not null)
				logger?.LogWarning("Encryption key unavailable for account secrets: {Error}", keyError);
		}

		// One memo across the whole build: declared users overwhelmingly inherit the SAME global role
		// ProviderSettings objects, so provider validation (CA reads, host probes) runs once per
		// distinct settings object rather than once per user (B7).
		ValidationMemo memo = new();
		try
		{
			// Every login declared at EITHER level, each resolved once. A login declared in both
			// is merged field by field — the database no longer replaces the whole config entry.
			Dictionary<string, UserOptions> configUsers = new(StringComparer.OrdinalIgnoreCase);
			foreach ((string login, UserOptions account) in options.Users ?? [])
				configUsers[login] = account;

			List<string> logins = configUsers.Keys
				.Concat(dbUsers?.Keys ?? Enumerable.Empty<string>())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			List<string> configOnlyFailures = new();
			foreach (string login in logins)
			{
				UserOptions? configEntry = configUsers.GetValueOrDefault(login);
				UserOptions? dbEntry = dbUsers?.GetValueOrDefault(login);
				UserMerge.Merged effective = UserMerge.Merge(configEntry, dbEntry);
				bool fromDatabase = dbEntry is not null;
				bool shadows = fromDatabase && configEntry is not null;

				List<string> failures = new();
				ValidateLogin(login, failures);
				UserTemplate template = BuildOne(
					roles, registry, login, effective.Options, key, failures, memo);

				if (failures.Count == 0)
				{
					templates[login] = template;
					merged[login] = new MergedUser(
						effective.Options, fromDatabase, shadows, Invalid: false, effective.Sources);
					continue;
				}

				if (!fromDatabase)
				{
					// Pure config: startup validation already rejected these, so reaching here means
					// direct construction (tests). Collect and throw, as before.
					configOnlyFailures.AddRange(failures);
					continue;
				}

				// B3: fail closed. Skipping the entry left NO template, so Resolve degraded to
				// pass-through (presented credentials forwarded verbatim, the overrides discarded)
				// and — with no merged entry — IsLoginDisabled UN-disabled an Enabled=false user.
				// Register an invalid sentinel that refuses resolution and keep the entry in the
				// merged view: operators still see it, and IsLoginDisabled still honours
				// Enabled==false (evaluated on the merged values, before validation), while the
				// login is refused until the declaration is corrected.
				logger?.LogWarning(
					"Refusing invalid database account entry for {User} (fail-closed) until corrected: {Failures}",
					login, string.Join("; ", failures));
				templates[login] = new UserTemplate(
					null, null, new Dictionary<BackendRole, RoleTemplate>(), Invalid: true);
				merged[login] = new MergedUser(
					effective.Options, true, shadows, Invalid: true, effective.Sources);
			}

			if (configOnlyFailures.Count > 0)
				throw new InvalidOperationException(string.Join(Environment.NewLine, configOnlyFailures));
		}
		finally
		{
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
		}

		return new Snapshot(templates.Count > 0 ? templates : null, merged);
	}

	/// <summary>Merges one entry against the global role assignments, collecting validation failures.</summary>
	private static UserTemplate BuildOne(
		BackendRolesConfig roles, BackendProviderRegistry registry, string login,
		UserOptions account, byte[]? encryptionKey, List<string> failures, ValidationMemo memo)
	{
		if (account.Password is not null &&
		    GatewayPasswordHasher.IsHashed(account.Password) &&
		    !GatewayPasswordHasher.TryParse(account.Password, out string? parseError))
			failures.Add($"ActiveSync:Users:{login}: Password is not a valid pbkdf2$ value: {parseError}.");
		// B18: a sealed enc:v1: value in the gateway Password position never authenticates —
		// VerifyLocally treats a non-pbkdf2$ value as plaintext and compares digests, so the real
		// password never matches and the account is silently locked out. Flag it instead of
		// letting it through unreported (the gateway Password wants pbkdf2$ or plaintext).
		if (account.Password is not null && SecretValue.IsSealed(account.Password))
			failures.Add($"ActiveSync:Users:{login}: the gateway Password is an enc:v1: sealed value, " +
			             "which never authenticates — use a pbkdf2$ hash (eas user password) or plaintext.");

		// Overrides keyed by role name; unknown keys are configuration mistakes, not silence.
		Dictionary<BackendRole, BackendRoleOverride> overrides = new();
		foreach ((string roleName, BackendRoleOverride roleOverride) in account.Backends ?? [])
		{
			if (!Enum.TryParse(roleName, true, out BackendRole role))
			{
				failures.Add(
					$"ActiveSync:Users:{login}:Backends:{roleName} is not a backend role " +
					$"(roles: {string.Join(", ", Enum.GetNames<BackendRole>())}).");
				continue;
			}

			if (!overrides.TryAdd(role, roleOverride))
				failures.Add($"ActiveSync:Users:{login}:Backends declares the {role} role twice.");
		}

		Dictionary<BackendRole, RoleTemplate> templates = new();
		foreach (BackendRole role in Enum.GetValues<BackendRole>())
		{
			RoleAssignment? global = roles.Assignments.GetValueOrDefault(role);
			BackendRoleOverride? user = overrides.GetValueOrDefault(role);
			if (user is null)
			{
				if (global is not null)
					templates[role] = new RoleTemplate(role, global.ProviderName, global.Settings, null, null);
				continue;
			}

			if (user.Enabled == false)
			{
				if (role is BackendRole.MailStore or BackendRole.MailSubmit)
				{
					failures.Add($"ActiveSync:Users:{login}:Backends:{role}: Enabled=false is not valid " +
					             "for the mail roles — the gateway cannot run without mail access.");
					continue;
				}

				// Content roles fall back to the gateway database; Oof turns off entirely.
				if (role != BackendRole.Oof)
					templates[role] = new RoleTemplate(role, "local", ProviderSettings.Empty, null, null);
				continue;
			}

			// B17: the global Provider is trimmed at load; normalize the per-user override the same
			// way so " imap" (or "") doesn't fail the inheritance equality check, drop the inherited
			// settings, and then throw an unrelated "unknown provider" from registry.GetFor.
			string? overrideProvider = string.IsNullOrWhiteSpace(user.Provider) ? null : user.Provider.Trim();

			if (global is null && overrideProvider is null && role == BackendRole.Oof)
			{
				failures.Add($"ActiveSync:Users:{login}:Backends:Oof: no global Oof role is configured — " +
				             "set Provider (e.g. \"sieve\") to enable it for this user.");
				continue;
			}

			// B21: an unconfigured gateway (no global mail role) must still construct — the invariant
			// is "start unconfigured so the UI can configure it". A user mail override with no global
			// and no explicit provider used to fall through to providerName "local", whose
			// registry.GetFor(MailStore) throws — crashing the resolver ctor for a config user and
			// misdiagnosing as "provider 'local' does not support MailStore". Mirror the Oof handling.
			if (global is null && overrideProvider is null &&
			    role is BackendRole.MailStore or BackendRole.MailSubmit)
			{
				failures.Add($"ActiveSync:Users:{login}:Backends:{role}: no global {role} role is configured — " +
				             "set Provider (e.g. \"imap\") to enable it for this user.");
				continue;
			}

			string providerName = overrideProvider ?? global?.ProviderName ?? "local";
			// Settings inherit the global section ONLY when the provider is unchanged — a
			// switched provider's keys mean something else entirely.
			bool inheritGlobal = global is not null &&
			                     providerName.Equals(global.ProviderName, StringComparison.OrdinalIgnoreCase);
			ProviderSettings settings = MergeSettings(
				inheritGlobal ? global!.Settings : null, user.Settings, login, role, failures);
			string? password = ResolveSecret(user.Password, encryptionKey, $"{login}:{role}", failures);
			templates[role] = new RoleTemplate(role, providerName, settings,
				string.IsNullOrWhiteSpace(user.UserName) ? null : user.UserName, password);
		}

		// Provider-delegated validation of every effective role — memoized per (provider, role,
		// settings-identity) so shared global settings are validated once, not once per user (B7).
		foreach ((BackendRole role, RoleTemplate template) in templates)
		{
			ValidationMemo.Outcome outcome = memo.Validate(registry, template.ProviderName, role, template.Settings);
			foreach (string failure in outcome.DirectFailures)
				failures.Add(failure);
			if (outcome.GetForError is not null)
				failures.Add($"ActiveSync:Users:{login}:Backends:{role}: {outcome.GetForError}");
		}

		// The user-default backend secret is unsealed exactly like a per-role one (B5 residency
		// note applies equally); the gateway Password deliberately is NOT — it is a local
		// verifier, and B18 flags a sealed value there as a configuration error.
		string? defaultBackendPassword = ResolveSecret(
			account.DefaultBackendPassword, encryptionKey, $"{login}:DefaultBackendPassword", failures);

		RequireGatewayPasswordForStoredMailSecret(login, account, overrides, failures);

		return new UserTemplate(
			string.IsNullOrWhiteSpace(account.Password) ? null : account.Password,
			string.IsNullOrWhiteSpace(account.MailAddress) ? null : account.MailAddress.Trim(),
			templates,
			Invalid: false,
			string.IsNullOrWhiteSpace(account.DefaultBackendLogin) ? null : account.DefaultBackendLogin.Trim(),
			defaultBackendPassword);
	}

	/// <summary>
	///   THE PROBE INVARIANT: a login may only be decided by the MailStore probe when the password
	///   that probe sends is the one the DEVICE presented. Authentication otherwise stops meaning
	///   anything — the probe would sign in with the gateway's own stored copy and return success
	///   for whatever the device typed, empty string included.
	///   <para>
	///     So a stored MailStore secret (the role override, or <see cref="UserOptions.DefaultBackendPassword" />
	///     which every role falls back to) REQUIRES a gateway <see cref="UserOptions.Password" />,
	///     and the two halves are one rule: adding the backend secret without a gateway password is
	///     refused, and so is removing the gateway password while one is present. Only MailStore
	///     matters here — it is the probe target. A Calendar- or MailSubmit-only secret leaves the
	///     probe reading the presented credential, so it needs no gateway password.
	///   </para>
	///   <para>
	///     Refusing the combination outright is what lets the two credential chains stay genuinely
	///     separate: a backend secret is never compared against a device password, anywhere. The
	///     alternative — comparing the presented value against the stored backend secret — closes
	///     the same hole but silently promotes a GATEWAY → BACKENDS credential into a
	///     DEVICE → GATEWAY one, and has to be re-derived correctly every time a credential tier is
	///     added. This cannot rot: the state is simply unrepresentable.
	///   </para>
	/// </summary>
	private static void RequireGatewayPasswordForStoredMailSecret(
		string login, UserOptions account,
		Dictionary<BackendRole, BackendRoleOverride> overrides, List<string> failures)
	{
		if (!string.IsNullOrWhiteSpace(account.Password))
			return;

		// The DECLARED value, not the unsealed one: a secret that failed to unseal has already
		// reported itself, and presence is what decides the hazard either way.
		bool roleSecret = !string.IsNullOrWhiteSpace(
			overrides.GetValueOrDefault(BackendRole.MailStore)?.Password);
		bool defaultSecret = !string.IsNullOrWhiteSpace(account.DefaultBackendPassword);
		if (!roleSecret && !defaultSecret)
			return;

		string stored = roleSecret ? $"Backends:{BackendRole.MailStore}:Password" : "DefaultBackendPassword";
		failures.Add(
			$"ActiveSync:Users:{login}: {stored} is set but no gateway Password is — the gateway would " +
			"then authenticate the device by signing in to the mail server with its OWN stored password, " +
			"which succeeds whatever the device sends. Set a gateway password first " +
			$"(eas user password {login}), or remove {stored} to go back to pass-through.");
	}

	/// <summary>
	///   Global role section flattened ⊕ the user's flat keys. Any user key replaces the
	///   whole global subtree it addresses — for list keys ("X:0") the numeric tail is
	///   stripped first so a shorter user list can never inherit trailing global elements.
	/// </summary>
	private static ProviderSettings MergeSettings(
		ProviderSettings? global, Dictionary<string, string?>? userSettings,
		string login, BackendRole role, List<string> failures)
	{
		if (userSettings is not { Count: > 0 })
			return global ?? ProviderSettings.Empty;

		Dictionary<string, string?> flat = new(StringComparer.OrdinalIgnoreCase);
		if (global is not null)
			foreach (KeyValuePair<string, string?> pair in global.Section.AsEnumerable(true))
				if (pair.Value is not null)
					flat[pair.Key] = pair.Value;
		flat.Remove(BackendRolesConfig.ProviderKey);

		foreach (string userKey in userSettings.Keys)
		{
			if (userKey.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
			{
				failures.Add(
					$"ActiveSync:Users:{login}:Backends:{role}:Settings: 'Provider' is not a setting — " +
					"use the Provider field of the override.");
				continue;
			}

			string root = BackendConfigValidation.ListRoot(userKey);
			foreach (string existing in flat.Keys
				         .Where(k => k.Equals(root, StringComparison.OrdinalIgnoreCase) ||
				                     k.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase))
				         .ToList())
				flat.Remove(existing);
		}

		foreach ((string userKey, string? value) in userSettings)
			if (value is not null && !userKey.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
				flat[userKey] = value;

		return ProviderSettings.FromFlat(flat);
	}

	/// <summary>
	///   The rules every login has to satisfy, wherever it is written. Public because the same
	///   text is also stored by paths that do not create an account — device blocks and shared
	///   calendar grants — where an unchecked value becomes a row that can never match.
	/// </summary>
	public static void ValidateLogin(string login, List<string> failures)
	{
		if (string.IsNullOrWhiteSpace(login))
		{
			failures.Add("ActiveSync:Users contains an empty login.");
			return;
		}

		// ':' cannot survive Basic auth (split on first ':'); '\n' and other control chars
		// would corrupt the session/watcher key separator and the encryption AAD.
		if (login.Contains(':') || login.Any(char.IsControl))
			failures.Add($"ActiveSync:Users:{login}: login must not contain ':' or control characters.");
	}

	/// <summary>
	///   Unseals one <c>enc:v1:</c> backend secret at snapshot-build time. B5: the returned plaintext
	///   is stored in the compiled <see cref="RoleTemplate" /> and stays resident in GC-managed memory
	///   for the whole lifetime of the immutable <see cref="Snapshot" /> (bounded by
	///   <see cref="AuthOptions.UsersRefreshSeconds" /> — the snapshot is replaced, and the old one
	///   becomes collectible, on the next rebuild) rather than being re-derived per <see cref="Resolve" />
	///   call. This is a deliberate, ACCEPTED residency, not an oversight: unsealing lazily at
	///   `Resolve` time would need the master key loaded (and potentially PBKDF2-stretched from a
	///   passphrase — K3) on every request that resolves an account with a sealed backend secret,
	///   trading a memory-residency window (already gated behind reading process memory with the
	///   master key available) for a real per-request cost, for a Low-severity finding. Unlike the
	///   master key itself (zeroed after use, see the `finally` in <see cref="BuildSnapshot" />), the
	///   values returned here are NOT zeroed — they are ordinary heap strings, and .NET has no way to
	///   scrub a `string`'s backing memory deterministically anyway.
	/// </summary>
	private static string? ResolveSecret(
		string? value, byte[]? encryptionKey, string context, List<string> failures)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;
		if (!SecretValue.IsSealed(value))
			return value;

		if (encryptionKey is null)
		{
			failures.Add(
				$"ActiveSync:Users:{context}:Password is sealed (enc:v1:) but no ActiveSync:Encryption " +
				"key is configured — sealed values require the master key even with AllowPlaintext.");
			return null;
		}

		if (!SecretValue.TryUnseal(value, encryptionKey, out string? plaintext, out string? error))
		{
			failures.Add($"ActiveSync:Users:{context}:Password could not be unsealed — {error}.");
			return null;
		}

		return plaintext;
	}

	/// <summary>
	///   B7: memoizes provider <c>ValidateConfiguration</c> output within one snapshot build. Almost
	///   every declared user inherits the SAME global role <see cref="ProviderSettings" /> object (an
	///   unset role, or a role whose Settings are not overridden, reuses the assignment's Settings
	///   reference), so keying on settings identity collapses O(users × roles) provider validations —
	///   each of which re-reads the CA file and re-checks the section — to O(distinct settings × roles).
	///   The provider's output is login-independent, so this is behaviour-preserving; the one
	///   login-specific part (the <c>GetFor</c> failure prefix) is applied by the caller.
	/// </summary>
	private sealed class ValidationMemo
	{
		internal sealed record Outcome(IReadOnlyList<string> DirectFailures, string? GetForError);

		private static readonly IReadOnlyList<string> None = [];

		private readonly Dictionary<(string Provider, BackendRole Role, ProviderSettings Settings), Outcome> _cache =
			new(KeyComparer.Instance);

		public Outcome Validate(
			BackendProviderRegistry registry, string providerName, BackendRole role, ProviderSettings settings)
		{
			(string, BackendRole, ProviderSettings) key = (providerName, role, settings);
			if (_cache.TryGetValue(key, out Outcome? cached))
				return cached;

			Outcome outcome;
			try
			{
				List<string> local = new();
				registry.GetFor(providerName, role).ValidateConfiguration(role, settings, local);
				outcome = new Outcome(local.Count == 0 ? None : local, null);
			}
			catch (InvalidOperationException ex)
			{
				outcome = new Outcome(None, ex.Message);
			}

			_cache[key] = outcome;
			return outcome;
		}

		private sealed class KeyComparer
			: IEqualityComparer<(string Provider, BackendRole Role, ProviderSettings Settings)>
		{
			public static readonly KeyComparer Instance = new();

			public bool Equals(
				(string Provider, BackendRole Role, ProviderSettings Settings) a,
				(string Provider, BackendRole Role, ProviderSettings Settings) b) =>
				a.Role == b.Role &&
				ReferenceEquals(a.Settings, b.Settings) &&
				string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase);

			public int GetHashCode((string Provider, BackendRole Role, ProviderSettings Settings) key) =>
				HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(key.Provider),
					key.Role,
					RuntimeHelpers.GetHashCode(key.Settings));
		}
	}

	/// <summary>Configured (non-inherited) parts of one role: unset = inherit at resolve time.</summary>
	private sealed record RoleTemplate(
		BackendRole Role, string ProviderName, ProviderSettings Settings, string? UserName, string? Password);

	/// <summary>
	///   One compiled user. <see cref="GatewayPassword" /> is the DEVICE → GATEWAY credential
	///   (verified locally, never sent anywhere); <see cref="DefaultBackendLogin" /> /
	///   <see cref="DefaultBackendPassword" /> are the GATEWAY → BACKENDS defaults every role
	///   falls back to. Two trust domains, kept as separate members so neither can be mistaken
	///   for the other at a call site.
	/// </summary>
	private sealed record UserTemplate(
		string? GatewayPassword,
		string? MailAddress,
		IReadOnlyDictionary<BackendRole, RoleTemplate> Roles,
		bool Invalid = false,
		string? DefaultBackendLogin = null,
		string? DefaultBackendPassword = null);

	/// <summary>Immutable compiled view, swapped atomically on database changes.</summary>
	private sealed record Snapshot(
		Dictionary<string, UserTemplate>? Templates,
		IReadOnlyDictionary<string, MergedUser> Users);
}
