using System.Security.Cryptography;
using System.Text;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>One entry of the merged (config ⊕ database) user view, for banners and the CLI.</summary>
public sealed record MergedAccount(AccountOptions Options, bool FromDatabase, bool ShadowsConfig);

/// <summary>
///   Maps a gateway login to its effective backend endpoints and credentials. Pass-through
///   is the baseline: undeclared logins use the global sections with the presented
///   credentials everywhere. A declared entry is a pure overlay — only the fields it sets
///   differ, and unset passwords inherit the presented EAS password per backend. Entries
///   come from config (<see cref="ActiveSyncOptions.Users" />, restart to change) and the
///   database (<see cref="AccountStore" />, a row REPLACES the whole config entry for that
///   login). The compiled snapshot is immutable and swapped atomically; database changes
///   are noticed via the AccountsStamp point-read at most every
///   <see cref="AuthOptions.UsersRefreshSeconds" />. Registered as a singleton.
/// </summary>
public sealed class AccountResolver
{
	private readonly ActiveSyncOptions _options;
	private readonly AccountStore? _store;
	private readonly ILogger<AccountResolver>? _logger;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private volatile Snapshot _snapshot;
	private long _nextCheckTicks;
	private Guid? _lastStamp;
	private bool _refreshErrorLogged;

	/// <summary>Raised after the snapshot was rebuilt from a database change (caches should reset).</summary>
	public event Action? SnapshotChanged;

	public AccountResolver(
		IOptions<ActiveSyncOptions> options,
		AccountStore? store = null,
		ILogger<AccountResolver>? logger = null)
	{
		_options = options.Value;
		_store = store;
		_logger = logger;
		// Config-only snapshot first; database entries arrive with the first EnsureFreshAsync
		// (the server forces one right after migrations, before any request).
		_snapshot = BuildSnapshot(_options, null, logger);
	}

	/// <summary>The merged, effective user view (database entries replacing config ones).</summary>
	public IReadOnlyDictionary<string, MergedAccount> MergedUsers => _snapshot.Users;

	/// <summary>
	///   Reloads database accounts when the change stamp moved. Cost when idle: one
	///   primary-key point-read at most every UsersRefreshSeconds, on the calling request.
	///   Failures keep the current snapshot (auth never goes down with the database).
	/// </summary>
	public async Task EnsureFreshAsync(bool force, CancellationToken ct)
	{
		if (_store is null)
			return;
		double refreshSeconds = _options.Auth.UsersRefreshSeconds;
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
				_snapshot = BuildSnapshot(_options, dbUsers, _logger);
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
	///   Local gateway-password verdict, or null when only an IMAP login probe can decide.
	///   Precedence: explicit gateway Password (hash/plaintext) → configured Imap:Password
	///   (presented must equal it) → null (probe). Undeclared logins: definitive false when
	///   <see cref="ActiveSyncOptions.RequireDeclaredUsers" /> is set, else null.
	/// </summary>
	public bool? VerifyLocally(string login, string presented)
	{
		AccountTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
			return _options.RequireDeclaredUsers ? false : null;
		if (template.GatewayPassword is not null)
			return GatewayPasswordHasher.Verify(template.GatewayPassword, presented);
		if (template.Imap.Password is not null)
			return TimingSafeEquals(template.Imap.Password, presented);
		return null;
	}

	/// <summary>Effective account for the presented credentials; never null.</summary>
	public ResolvedAccount Resolve(BackendCredentials presented)
	{
		string login = presented.UserName;
		AccountTemplate? template = _snapshot.Templates?.GetValueOrDefault(login);
		if (template is null)
			// Pass-through: same credentials everywhere, global endpoints, login-as-address-if-@.
			return new ResolvedAccount(
				login,
				login.Contains('@') ? login : null,
				false,
				new ResolvedBackend<ImapOptions>(_options.Imap, presented),
				new ResolvedBackend<SmtpOptions>(_options.Smtp, presented),
				_options.CalDav is null
					? null
					: new ResolvedBackend<DavServerOptions>(_options.CalDav, presented),
				_options.CardDav is null
					? null
					: new ResolvedBackend<DavServerOptions>(_options.CardDav, presented),
				_options.Sieve.Enabled
					? new ResolvedBackend<SieveOptions>(
						WithSieveHostDefault(_options.Sieve, _options.Imap.Host), presented)
					: null);

		// Credential inheritance: IMAP anchors on (override ?? login, override ?? presented);
		// SMTP and DAV default to the effective IMAP pair.
		string imapUser = template.Imap.UserName ?? login;
		string imapPassword = template.Imap.Password ?? presented.Password;
		return new ResolvedAccount(
			login,
			template.MailAddress ?? (login.Contains('@') ? login : null),
			template.MailAddress is not null,
			new ResolvedBackend<ImapOptions>(template.Imap.Options, new BackendCredentials(imapUser, imapPassword)),
			new ResolvedBackend<SmtpOptions>(template.Smtp.Options,
				new BackendCredentials(template.Smtp.UserName ?? imapUser, template.Smtp.Password ?? imapPassword)),
			ResolveDav(template.CalDav, imapUser, imapPassword),
			ResolveDav(template.CardDav, imapUser, imapPassword),
			template.Sieve is null
				? null
				: new ResolvedBackend<SieveOptions>(template.Sieve.Options,
					new BackendCredentials(template.Sieve.UserName ?? imapUser, template.Sieve.Password ?? imapPassword)));
	}

	/// <summary>The Sieve host default is "same box as IMAP" — filled in without mutating the singleton options.</summary>
	private static SieveOptions WithSieveHostDefault(SieveOptions sieve, string imapHost)
	{
		if (!string.IsNullOrWhiteSpace(sieve.Host))
			return sieve;
		return new SieveOptions
		{
			Enabled = sieve.Enabled,
			Host = imapHost,
			Port = sieve.Port,
			UseTls = sieve.UseTls,
			AllowInvalidCertificates = sieve.AllowInvalidCertificates,
			CaCertificatePath = sieve.CaCertificatePath
		};
	}

	/// <summary>Validation entry point — same merge/unseal code the runtime templates use.</summary>
	public static void ValidateUsers(ActiveSyncOptions options, byte[]? encryptionKey, List<string> failures)
	{
		BuildAll(options, encryptionKey, failures);
	}

	/// <summary>
	///   Validates one would-be entry (CLI writes) against the global options — identical
	///   rules to config entries. Returns the failure messages; empty = valid.
	/// </summary>
	public static List<string> ValidateEntry(ActiveSyncOptions options, string login, AccountOptions entry)
	{
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
		List<string> failures = new();
		if (keyError is not null)
			failures.Add(keyError);
		ValidateLogin(login, failures);
		BuildOne(options, login, entry, key, failures);
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
		ActiveSyncOptions options, Dictionary<string, AccountOptions>? dbUsers, ILogger? logger)
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
					templates[login] = BuildOne(options, login, account, key, failures);
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
					AccountTemplate template = BuildOne(options, login, account, key, failures);
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

	private static ResolvedBackend<DavServerOptions>? ResolveDav(
		BackendTemplate<DavServerOptions>? template, string imapUser, string imapPassword)
	{
		return template is null
			? null
			: new ResolvedBackend<DavServerOptions>(template.Options,
				new BackendCredentials(template.UserName ?? imapUser, template.Password ?? imapPassword));
	}

	private static Dictionary<string, AccountTemplate> BuildAll(
		ActiveSyncOptions options, byte[]? encryptionKey, List<string> failures)
	{
		Dictionary<string, AccountTemplate> templates = new(StringComparer.OrdinalIgnoreCase);
		if (options.Users is null)
			return templates;

		foreach ((string login, AccountOptions account) in options.Users)
		{
			ValidateLogin(login, failures);
			templates[login] = BuildOne(options, login, account, encryptionKey, failures);
		}

		return templates;
	}

	/// <summary>Merges one entry against the global sections, collecting validation failures.</summary>
	private static AccountTemplate BuildOne(
		ActiveSyncOptions options, string login, AccountOptions account,
		byte[]? encryptionKey, List<string> failures)
	{
		if (account.Password is not null &&
		    GatewayPasswordHasher.IsHashed(account.Password) &&
		    !GatewayPasswordHasher.TryParse(account.Password, out string? parseError))
			failures.Add($"ActiveSync:Users:{login}: Password is not a valid pbkdf2$ value: {parseError}.");

		ImapOptions imap = MergeImap(options.Imap, account.Imap);
		if (string.IsNullOrWhiteSpace(imap.Host))
			failures.Add(
				$"ActiveSync:Users:{login}: effective Imap:Host is empty — set it globally " +
				"under ActiveSync:Imap or override it for this user.");
		ValidatePort(imap.Port, login, "Imap", failures);

		SmtpOptions smtp = MergeSmtp(options.Smtp, account.Smtp);
		if (string.IsNullOrWhiteSpace(smtp.Host))
			failures.Add(
				$"ActiveSync:Users:{login}: effective Smtp:Host is empty — set it globally " +
				"under ActiveSync:Smtp or override it for this user.");
		ValidatePort(smtp.Port, login, "Smtp", failures);

		return new AccountTemplate(
			string.IsNullOrWhiteSpace(account.Password) ? null : account.Password,
			string.IsNullOrWhiteSpace(account.MailAddress) ? null : account.MailAddress.Trim(),
			new BackendTemplate<ImapOptions>(imap, account.Imap?.UserName,
				ResolveSecret(account.Imap?.Password, encryptionKey, $"{login}:Imap", failures)),
			new BackendTemplate<SmtpOptions>(smtp, account.Smtp?.UserName,
				ResolveSecret(account.Smtp?.Password, encryptionKey, $"{login}:Smtp", failures)),
			MergeDav(options.CalDav, account.CalDav, encryptionKey, login, "CalDav", failures),
			MergeDav(options.CardDav, account.CardDav, encryptionKey, login, "CardDav", failures),
			MergeSieve(options.Sieve, account.Sieve, imap.Host, encryptionKey, login, failures));
	}

	private static BackendTemplate<SieveOptions>? MergeSieve(
		SieveOptions global, SieveAccountOptions? user, string effectiveImapHost,
		byte[]? encryptionKey, string login, List<string> failures)
	{
		bool enabled = user?.Enabled ?? global.Enabled;
		if (!enabled)
			return null;

		SieveOptions merged = new()
		{
			Enabled = true,
			Host = user?.Host ?? global.Host,
			Port = user?.Port ?? global.Port,
			UseTls = user?.UseTls ?? global.UseTls,
			AllowInvalidCertificates = user?.AllowInvalidCertificates ?? global.AllowInvalidCertificates,
			CaCertificatePath = user?.CaCertificatePath ?? global.CaCertificatePath
		};
		if (string.IsNullOrWhiteSpace(merged.Host))
			merged.Host = effectiveImapHost;
		if (merged.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:Users:{login}: effective Sieve:Port {merged.Port} is out of range (1-65535).");

		return new BackendTemplate<SieveOptions>(merged, user?.UserName,
			ResolveSecret(user?.Password, encryptionKey, $"{login}:Sieve", failures));
	}

	private static void ValidateLogin(string login, List<string> failures)
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

	private static void ValidatePort(int port, string login, string section, List<string> failures)
	{
		if (port is < 1 or > 65535)
			failures.Add($"ActiveSync:Users:{login}: effective {section}:Port {port} is out of range (1-65535).");
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

	private static ImapOptions MergeImap(ImapOptions global, ImapAccountOptions? user)
	{
		return new ImapOptions
		{
			Host = user?.Host ?? global.Host,
			Port = user?.Port ?? global.Port,
			UseSsl = user?.UseSsl ?? global.UseSsl,
			Security = user?.Security ?? global.Security,
			AllowInvalidCertificates = user?.AllowInvalidCertificates ?? global.AllowInvalidCertificates,
			CaCertificatePath = user?.CaCertificatePath ?? global.CaCertificatePath,
			PathSeparator = user?.PathSeparator ?? global.PathSeparator
		};
	}

	private static SmtpOptions MergeSmtp(SmtpOptions global, SmtpAccountOptions? user)
	{
		return new SmtpOptions
		{
			Host = user?.Host ?? global.Host,
			Port = user?.Port ?? global.Port,
			UseSsl = user?.UseSsl ?? global.UseSsl,
			Security = user?.Security ?? global.Security,
			AllowInvalidCertificates = user?.AllowInvalidCertificates ?? global.AllowInvalidCertificates,
			CaCertificatePath = user?.CaCertificatePath ?? global.CaCertificatePath,
			ForceFrom = user?.ForceFrom ?? global.ForceFrom
		};
	}

	private static BackendTemplate<DavServerOptions>? MergeDav(
		DavServerOptions? global, DavAccountOptions? user,
		byte[]? encryptionKey, string login, string section, List<string> failures)
	{
		// Enabled: false is the explicit per-user off switch; otherwise the side exists when
		// there is a global section or the user brings their own BaseUrl.
		if (user?.Enabled == false)
			return null;
		if (global is null && string.IsNullOrWhiteSpace(user?.BaseUrl))
			return null;

		DavServerOptions merged = new()
		{
			BaseUrl = user?.BaseUrl ?? global?.BaseUrl ?? "",
			HomeSetPath = user?.HomeSetPath ?? global?.HomeSetPath,
			// "Tasks" is the section default; an explicit "" (user or global) disables tasks.
			TaskFolder = user?.TaskFolder ?? global?.TaskFolder ?? "Tasks",
			AllowInvalidCertificates = user?.AllowInvalidCertificates ?? global?.AllowInvalidCertificates ?? false,
			CaCertificatePath = user?.CaCertificatePath ?? global?.CaCertificatePath,
			CalendarAttachments = user?.CalendarAttachments ?? global?.CalendarAttachments ?? "Auto",
			SharedCollections = user?.SharedCollections ?? global?.SharedCollections,
			SendInvitations = user?.SendInvitations ?? global?.SendInvitations ?? "Auto"
		};
		if (merged.SendInvitations.ToLowerInvariant() is not ("auto" or "on" or "off"))
			failures.Add(
				$"ActiveSync:Users:{login}: effective {section}:SendInvitations " +
				$"'{merged.SendInvitations}' is unknown (use Auto, On or Off).");
		if (merged.CalendarAttachments.ToLowerInvariant() is not ("auto" or "on" or "off"))
			failures.Add(
				$"ActiveSync:Users:{login}: effective {section}:CalendarAttachments " +
				$"'{merged.CalendarAttachments}' is unknown (use Auto, On or Off).");
		foreach (string entry in merged.SharedCollections ?? [])
			if (Backend.SharedCollection.Validate(entry, merged.BaseUrl) is { } sharedFailure)
				failures.Add($"ActiveSync:Users:{login}: effective {section}:SharedCollections: {sharedFailure}");
		if (!Uri.TryCreate(merged.BaseUrl, UriKind.Absolute, out Uri? uri) ||
		    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			failures.Add(
				$"ActiveSync:Users:{login}: effective {section}:BaseUrl '{merged.BaseUrl}' " +
				"must be an absolute http(s) URL.");

		return new BackendTemplate<DavServerOptions>(merged, user?.UserName,
			ResolveSecret(user?.Password, encryptionKey, $"{login}:{section}", failures));
	}

	/// <summary>Length-independent comparison via fixed-size digests.</summary>
	private static bool TimingSafeEquals(string expected, string presented)
	{
		byte[] expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
		byte[] presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
		return CryptographicOperations.FixedTimeEquals(expectedDigest, presentedDigest);
	}

	/// <summary>Configured (non-inherited) parts of one backend: unset = inherit at resolve time.</summary>
	private sealed record BackendTemplate<TOptions>(TOptions Options, string? UserName, string? Password);

	private sealed record AccountTemplate(
		string? GatewayPassword,
		string? MailAddress,
		BackendTemplate<ImapOptions> Imap,
		BackendTemplate<SmtpOptions> Smtp,
		BackendTemplate<DavServerOptions>? CalDav,
		BackendTemplate<DavServerOptions>? CardDav,
		BackendTemplate<SieveOptions>? Sieve);

	/// <summary>Immutable compiled view, swapped atomically on database changes.</summary>
	private sealed record Snapshot(
		Dictionary<string, AccountTemplate>? Templates,
		IReadOnlyDictionary<string, MergedAccount> Users);
}
