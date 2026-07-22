using System.Security.Cryptography;
using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>One entry of the merged (config ⊕ database) user view, for banners and the CLI.</summary>
public sealed record MergedAccount(AccountOptions Options, bool FromDatabase, bool ShadowsConfig);

/// <summary>
///   Maps a gateway login to its effective backend roles and credentials. Pass-through is
///   the baseline: undeclared logins use the global role sections with the presented
///   credentials everywhere. A declared entry is a pure overlay — only the role fields it
///   sets differ, and unset passwords inherit the presented EAS password per role. Entries
///   come from config (<see cref="ActiveSyncOptions.Users" />, restart to change) and the
///   database (<see cref="AccountStore" />, a row REPLACES the whole config entry for that
///   login). The compiled snapshot is immutable and swapped atomically; database changes
///   are noticed via the AccountsStamp point-read at most every
///   <see cref="AuthOptions.UsersRefreshSeconds" />. Registered as a singleton.
/// </summary>
public sealed class AccountResolver
{
	private readonly IOptionsMonitor<ActiveSyncOptions> _options;
	private readonly BackendRolesProvider _rolesProvider;
	private readonly BackendProviderRegistry _registry;
	private readonly AccountStore? _store;
	private readonly ILogger<AccountResolver>? _logger;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private volatile Snapshot _snapshot;
	private long _nextCheckTicks;
	private Guid? _lastStamp;
	private Dictionary<string, AccountOptions>? _lastDbUsers;
	private bool _refreshErrorLogged;

	/// <summary>Raised after the snapshot was rebuilt from a database change (caches should reset).</summary>
	public event Action? SnapshotChanged;

	public AccountResolver(
		IOptionsMonitor<ActiveSyncOptions> options,
		BackendRolesProvider rolesProvider,
		BackendProviderRegistry registry,
		AccountStore? store = null,
		ILogger<AccountResolver>? logger = null)
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

	/// <summary>The merged, effective user view (database entries replacing config ones).</summary>
	public IReadOnlyDictionary<string, MergedAccount> MergedUsers => _snapshot.Users;

	/// <summary>
	///   True when <paramref name="login" /> is a declared account explicitly disabled
	///   (<see cref="AccountOptions.Enabled" /> == false) — a persistent refusal of every login,
	///   enforced at the endpoint like a user-level block. Reads the current in-memory snapshot, so
	///   it is cheap on the auth path; an undeclared/pass-through login has no row and is never
	///   "disabled" (block it instead). Case-insensitive, matching config/database key semantics.
	/// </summary>
	public bool IsLoginDisabled(string login) =>
		_snapshot.Users.TryGetValue(login, out MergedAccount? account) && account.Options.Enabled == false;

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
		if (!force)
		{
			if (refreshSeconds < 0)
				return;
			if (Environment.TickCount64 < Volatile.Read(ref _nextCheckTicks))
				return;
		}

		// A refresh already in flight serves this caller fine — use the current snapshot.
		if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
			return;
		try
		{
			Guid? stamp = await _store.ReadStampAsync(ct).ConfigureAwait(false);
			if (stamp != _lastStamp)
			{
				Dictionary<string, AccountOptions>? dbUsers = stamp is null
					? null
					: await _store.LoadAllAsync(_logger, ct).ConfigureAwait(false);
				_lastDbUsers = dbUsers;
				_snapshot = BuildSnapshot(_options.CurrentValue, _rolesProvider.Current, _registry, dbUsers, _logger);
				_lastStamp = stamp;
				_logger?.LogInformation(
					"Accounts snapshot rebuilt: {Count} declared user(s) ({Db} from database)",
					_snapshot.Users.Count, _snapshot.Users.Count(u => u.Value.FromDatabase));
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
			Volatile.Write(ref _nextCheckTicks,
				Environment.TickCount64 + (long)(Math.Max(refreshSeconds, 0) * 1000));
			_refreshGate.Release();
		}
	}

	/// <summary>
	///   Rebuilds the snapshot when the global backend role configuration changed (a live
	///   settings edit), so declared users inherit the new role settings. Uses the last-loaded
	///   database users; pass-through resolution already reads the current roles directly. The
	///   snapshot swap is atomic, so this needs no lock against the request-path refresh.
	/// </summary>
	private void OnRolesChanged()
	{
		_snapshot = BuildSnapshot(
			_options.CurrentValue, _rolesProvider.Current, _registry, _lastDbUsers, _logger);
		SnapshotChanged?.Invoke();
	}

	/// <summary>
	///   Local gateway-password verdict, or null when only a backend login probe can decide.
	///   Precedence: explicit gateway Password (hash/plaintext) → configured MailStore
	///   Password (presented must equal it) → null (probe). Undeclared logins: definitive
	///   false when <see cref="ActiveSyncOptions.RequireDeclaredUsers" /> is set, else null.
	/// </summary>
	public bool? VerifyLocally(string login, string presented)
	{
		AccountTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
			return _options.CurrentValue.RequireDeclaredUsers ? false : null;
		if (template.GatewayPassword is not null)
			return GatewayPasswordHasher.Verify(template.GatewayPassword, presented);
		if (template.Roles.GetValueOrDefault(BackendRole.MailStore)?.Password is { } mailPassword)
			return TimingSafeEquals(mailPassword, presented);
		return null;
	}

	/// <summary>Effective account for the presented credentials; never null.</summary>
	public ResolvedAccount Resolve(BackendCredentials presented)
	{
		string login = presented.UserName;
		AccountTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
		{
			// Pass-through: same credentials everywhere, the global role sections verbatim.
			Dictionary<BackendRole, ResolvedRole> passThrough = new();
			foreach ((BackendRole role, RoleAssignment assignment) in _rolesProvider.Current.Assignments)
				passThrough[role] = new ResolvedRole(role, assignment.ProviderName, assignment.Settings, presented);
			return new ResolvedAccount(
				login, login.Contains('@') ? login : null, false, passThrough);
		}

		// Credential inheritance: MailStore anchors on (override ?? login, override ?? presented);
		// every other role defaults to the effective MailStore pair.
		RoleTemplate? mailStore = template.Roles.GetValueOrDefault(BackendRole.MailStore);
		string mailUser = mailStore?.UserName ?? login;
		string mailPassword = mailStore?.Password ?? presented.Password;
		Dictionary<BackendRole, ResolvedRole> roles = new();
		foreach ((BackendRole role, RoleTemplate roleTemplate) in template.Roles)
			roles[role] = new ResolvedRole(role, roleTemplate.ProviderName, roleTemplate.Settings,
				role == BackendRole.MailStore
					? new BackendCredentials(mailUser, mailPassword)
					: new BackendCredentials(roleTemplate.UserName ?? mailUser, roleTemplate.Password ?? mailPassword));
		return new ResolvedAccount(
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
		foreach ((string login, AccountOptions account) in options.Users)
		{
			ValidateLogin(login, failures);
			BuildOne(roles, registry, login, account, encryptionKey, failures);
		}
	}

	/// <summary>
	///   Validates one would-be entry (CLI writes) against the global role sections —
	///   identical rules to config entries. Returns the failure messages; empty = valid.
	/// </summary>
	public static List<string> ValidateEntry(
		ActiveSyncOptions options, BackendRolesConfig roles, BackendProviderRegistry registry,
		string login, AccountOptions entry)
	{
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
		List<string> failures = new();
		if (keyError is not null)
			failures.Add(keyError);
		ValidateLogin(login, failures);
		BuildOne(roles, registry, login, entry, key, failures);
		if (key is not null)
			CryptographicOperations.ZeroMemory(key);
		return failures;
	}

	/// <summary>
	///   Compiles the immutable snapshot. Config entries are strict (invalid config already
	///   failed startup validation; direct construction throws). Database entries are
	///   lenient: an invalid entry is skipped with a warning so a bad row written by an
	///   older/newer CLI can never take authentication down.
	/// </summary>
	private static Snapshot BuildSnapshot(
		ActiveSyncOptions options, BackendRolesConfig roles, BackendProviderRegistry registry,
		Dictionary<string, AccountOptions>? dbUsers, ILogger? logger)
	{
		Dictionary<string, AccountTemplate> templates = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, MergedAccount> merged = new(StringComparer.OrdinalIgnoreCase);
		bool needKey = options.Users is { Count: > 0 } || dbUsers is { Count: > 0 };
		byte[]? key = null;
		if (needKey)
		{
			key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
			if (key is null && keyError is not null)
				logger?.LogWarning("Encryption key unavailable for account secrets: {Error}", keyError);
		}

		try
		{
			if (options.Users is { Count: > 0 })
			{
				List<string> failures = new();
				foreach ((string login, AccountOptions account) in options.Users)
				{
					ValidateLogin(login, failures);
					templates[login] = BuildOne(roles, registry, login, account, key, failures);
					merged[login] = new MergedAccount(account, false, false);
				}

				// Startup validation already rejected these; this guards direct construction in tests.
				if (failures.Count > 0)
					throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
			}

			if (dbUsers is { Count: > 0 })
			{
				foreach ((string login, AccountOptions account) in dbUsers)
				{
					List<string> failures = new();
					ValidateLogin(login, failures);
					AccountTemplate template = BuildOne(roles, registry, login, account, key, failures);
					if (failures.Count > 0)
					{
						logger?.LogWarning(
							"Skipping invalid database account entry for {User}: {Failures}",
							login, string.Join("; ", failures));
						continue;
					}

					bool shadows = merged.ContainsKey(login);
					templates[login] = template;
					merged[login] = new MergedAccount(account, true, shadows);
				}
			}
		}
		finally
		{
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
		}

		return new Snapshot(templates.Count > 0 ? templates : null, merged);
	}

	/// <summary>Merges one entry against the global role assignments, collecting validation failures.</summary>
	private static AccountTemplate BuildOne(
		BackendRolesConfig roles, BackendProviderRegistry registry, string login,
		AccountOptions account, byte[]? encryptionKey, List<string> failures)
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

			if (global is null && user.Provider is null && role == BackendRole.Oof)
			{
				failures.Add($"ActiveSync:Users:{login}:Backends:Oof: no global Oof role is configured — " +
				             "set Provider (e.g. \"sieve\") to enable it for this user.");
				continue;
			}

			string providerName = user.Provider ?? global?.ProviderName ?? "local";
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

		// Provider-delegated validation of every effective role.
		foreach ((BackendRole role, RoleTemplate template) in templates)
			try
			{
				registry.GetFor(template.ProviderName, role)
					.ValidateConfiguration(role, template.Settings, failures);
			}
			catch (InvalidOperationException ex)
			{
				failures.Add($"ActiveSync:Users:{login}:Backends:{role}: {ex.Message}");
			}

		return new AccountTemplate(
			string.IsNullOrWhiteSpace(account.Password) ? null : account.Password,
			string.IsNullOrWhiteSpace(account.MailAddress) ? null : account.MailAddress.Trim(),
			templates);
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

	/// <summary>Length-independent comparison via fixed-size digests.</summary>
	private static bool TimingSafeEquals(string expected, string presented)
	{
		byte[] expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
		byte[] presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
		return CryptographicOperations.FixedTimeEquals(expectedDigest, presentedDigest);
	}

	/// <summary>Configured (non-inherited) parts of one role: unset = inherit at resolve time.</summary>
	private sealed record RoleTemplate(
		BackendRole Role, string ProviderName, ProviderSettings Settings, string? UserName, string? Password);

	private sealed record AccountTemplate(
		string? GatewayPassword,
		string? MailAddress,
		IReadOnlyDictionary<BackendRole, RoleTemplate> Roles);

	/// <summary>Immutable compiled view, swapped atomically on database changes.</summary>
	private sealed record Snapshot(
		Dictionary<string, AccountTemplate>? Templates,
		IReadOnlyDictionary<string, MergedAccount> Users);
}
