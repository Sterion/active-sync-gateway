using System.ComponentModel;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Shared plumbing for the `user` branch: loads the store + global options, clones
///   shadowed config entries so single-field edits keep the rest, validates every write
///   with the exact rules config entries face, and prints the masked result.
/// </summary>
internal abstract class UserCommandBase<TSettings>(IAnsiConsole terminal) : DatabaseCommand<TSettings>(terminal)
	where TSettings : CommandSettings
{
	protected BackendRolesConfig Roles { get; private set; } = null!;
	protected BackendProviderRegistry Registry { get; private set; } = null!;

	protected sealed override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, TSettings settings, CancellationToken cancellationToken)
	{
		AccountStore store = services.GetRequiredService<AccountStore>();
		ActiveSyncOptions options = services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value;
		Roles = services.GetRequiredService<BackendRolesConfig>();
		Registry = services.GetRequiredService<BackendProviderRegistry>();
		return await RunAsync(store, options, settings, cancellationToken);
	}

	protected abstract Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, TSettings settings, CancellationToken cancellationToken);

	protected static AccountOptions Clone(AccountOptions source)
	{
		return JsonSerializer.Deserialize<AccountOptions>(
			JsonSerializer.Serialize(source, AccountStore.JsonOptions), AccountStore.JsonOptions)!;
	}

	/// <summary>DB entry, else a copy of the config entry, else a fresh one.</summary>
	protected static async Task<AccountOptions> LoadStartingEntryAsync(
		AccountStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		if (await store.GetAsync(login, ct) is { } fromDb)
			return fromDb;
		return options.Users?.GetValueOrDefault(login) is { } fromConfig
			? Clone(fromConfig)
			: new AccountOptions();
	}

	/// <summary>Validates, saves and reports; refuses invalid entries with config-grade messages.</summary>
	protected async Task<int> ValidateAndSaveAsync(
		AccountStore store, ActiveSyncOptions options, string login, AccountOptions entry, CancellationToken ct)
	{
		List<string> failures = AccountResolver.ValidateEntry(options, Roles, Registry, login, entry);
		if (failures.Count > 0)
		{
			await Console.Error.WriteLineAsync("The entry would be invalid — nothing was saved:");
			foreach (string failure in failures)
				await Console.Error.WriteLineAsync($"  - {failure}");
			return 1;
		}

		await store.UpsertAsync(login, entry, ct);
		Terminal.WriteLine($"{login}  {StartupSummary.DescribeUser(new MergedAccount(entry, true, options.Users?.ContainsKey(login) == true))}");
		Terminal.WriteLine(PickupNote(options));
		return 0;
	}

	protected static string PickupNote(ActiveSyncOptions options)
	{
		return options.Auth.UsersRefreshSeconds < 0
			? "Live refresh is disabled (Auth:UsersRefreshSeconds < 0) — restart the gateway to apply."
			: $"A running gateway picks this up within ~{Math.Max(options.Auth.UsersRefreshSeconds, 1):0}s.";
	}
}

internal sealed class UserListCommand(IAnsiConsole terminal) : UserCommandBase<UserListCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings;

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		List<(string UserName, AccountOptions Options, DateTime UpdatedUtc)> dbEntries =
			await store.ListAsync(cancellationToken);
		Dictionary<string, AccountOptions> configUsers =
			options.Users ?? new Dictionary<string, AccountOptions>(StringComparer.OrdinalIgnoreCase);

		SortedSet<string> logins = new(StringComparer.OrdinalIgnoreCase);
		logins.UnionWith(configUsers.Keys);
		logins.UnionWith(dbEntries.Select(e => e.UserName));
		if (logins.Count == 0)
		{
			Terminal.WriteLine("No users are declared (config or database) — pure IMAP pass-through.");
			return 0;
		}

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("Login", "Origin", "Mail", "Gateway pw", "Overrides");
		foreach (string login in logins)
		{
			bool inDb = dbEntries.Any(e => string.Equals(e.UserName, login, StringComparison.OrdinalIgnoreCase));
			bool inConfig = configUsers.ContainsKey(login);
			AccountOptions effective = inDb
				? dbEntries.First(e => string.Equals(e.UserName, login, StringComparison.OrdinalIgnoreCase)).Options
				: configUsers[login];
			string origin = inDb ? inConfig ? "db (shadows config)" : "db" : "config";
			string password = string.IsNullOrWhiteSpace(effective.Password)
				? "-"
				: GatewayPasswordHasher.IsHashed(effective.Password) ? "***(pbkdf2)" : "***(PLAINTEXT)";
			List<string> sections = [];
			foreach ((string roleName, BackendRoleOverride roleOverride) in
			         (effective.Backends ?? []).OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
				sections.Add(roleOverride.Enabled == false
					? $"{roleName.ToLowerInvariant()}=off"
					: roleOverride.Provider is { } switched
						? $"{roleName.ToLowerInvariant()}={switched}"
						: roleName.ToLowerInvariant());
			AddRow(table, login, origin, effective.MailAddress ?? "-", password,
				sections.Count > 0 ? string.Join(", ", sections) : "-");
		}

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class UserShowCommand(IAnsiConsole terminal) : UserCommandBase<UserShowCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		AccountOptions? fromDb = await store.GetAsync(settings.Login, cancellationToken);
		AccountOptions? fromConfig = options.Users?.GetValueOrDefault(settings.Login);
		if (fromDb is null && fromConfig is null)
		{
			await Console.Error.WriteLineAsync($"No declared user '{settings.Login}' (config or database).");
			return 1;
		}

		MergedAccount effective = fromDb is not null
			? new MergedAccount(fromDb, true, fromConfig is not null)
			: new MergedAccount(fromConfig!, false, false);
		Terminal.WriteLine($"{settings.Login}  {StartupSummary.DescribeUser(effective)}");
		if (effective is { FromDatabase: true, ShadowsConfig: true })
			Terminal.WriteLine(
				"A config entry for this login exists but is fully replaced by the database entry " +
				"('eas user remove' falls back to it).");
		return 0;
	}
}

internal sealed class UserAddCommand(IAnsiConsole terminal) : UserCommandBase<UserAddCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		[Description("The gateway login (what the phone authenticates as).")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		if (await store.GetAsync(settings.Login, cancellationToken) is not null)
		{
			await Console.Error.WriteLineAsync(
				$"A database entry for '{settings.Login}' already exists — use 'eas user set/show'.");
			return 1;
		}

		// A brand-new entry is an empty overlay (an allowlist grant); when a config entry
		// exists it is copied so the database version starts as an exact replacement.
		AccountOptions entry = options.Users?.GetValueOrDefault(settings.Login) is { } fromConfig
			? Clone(fromConfig)
			: new AccountOptions();
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}

internal sealed class UserRemoveCommand(IAnsiConsole terminal) : UserCommandBase<UserRemoveCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		if (!await store.DeleteAsync(settings.Login, cancellationToken))
		{
			await Console.Error.WriteLineAsync($"No database entry for '{settings.Login}' — nothing to remove.");
			return 1;
		}

		Terminal.WriteLine(options.Users?.ContainsKey(settings.Login) == true
			? $"Removed the database entry for '{settings.Login}' — the config entry is active again."
			: $"Removed the database entry for '{settings.Login}'.");
		Terminal.WriteLine(PickupNote(options));
		return 0;
	}
}

internal sealed class UserSetCommand(IAnsiConsole terminal) : UserCommandBase<UserSetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }

		[CommandArgument(1, "<key>")]
		[Description("Field path, e.g. MailAddress, Backends:MailStore:Settings:Host, Backends:Calendar:Enabled.")]
		public required string Key { get; init; }

		[CommandArgument(2, "<value>")]
		public required string Value { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		AccountFieldPaths.FieldPath? field = AccountFieldPaths.Find(settings.Key);
		if (field is null)
		{
			await Console.Error.WriteLineAsync(
				$"Unknown field '{settings.Key}'. Valid fields: {string.Join(", ", AccountFieldPaths.Keys)}");
			return 1;
		}

		object? value;
		if (field.IsSecret)
		{
			string? prepared = await PrepareSecretAsync(field, settings.Value, options);
			if (prepared is null)
				return 1;
			value = prepared;
		}
		else if (!AccountFieldPaths.TryParseValue(field, settings.Value, out value, out string? parseError))
		{
			await Console.Error.WriteLineAsync(parseError);
			return 1;
		}

		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		field.Set(entry, value);
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}

	/// <summary>
	///   Password keys on argv: an already-prepared value (pbkdf2$/enc:v1:) is stored as-is;
	///   plaintext is hashed (gateway Password) or sealed (backend passwords) with a warning —
	///   the stdin commands ('user password'/'user secret') keep secrets out of shell history.
	/// </summary>
	private static async Task<string?> PrepareSecretAsync(
		AccountFieldPaths.FieldPath field, string raw, ActiveSyncOptions options)
	{
		bool isGatewayPassword = !field.Key.Contains(':');
		if (isGatewayPassword)
		{
			if (GatewayPasswordHasher.IsHashed(raw))
			{
				if (!GatewayPasswordHasher.TryParse(raw, out string? error))
				{
					await Console.Error.WriteLineAsync($"Not a valid pbkdf2$ value: {error}");
					return null;
				}

				return raw;
			}

			if (SecretValue.IsSealed(raw))
			{
				await Console.Error.WriteLineAsync(
					"The gateway Password takes a pbkdf2$ hash (or plaintext), not an enc:v1: sealed value.");
				return null;
			}

			await Console.Error.WriteLineAsync(
				"Warning: plaintext password on the command line (visible in shell history/ps) — " +
				"prefer: echo -n '...' | eas user password <login>. Stored as a pbkdf2$ hash.");
			return GatewayPasswordHasher.Hash(raw);
		}

		if (SecretValue.IsSealed(raw))
			return raw;
		if (GatewayPasswordHasher.IsHashed(raw))
		{
			await Console.Error.WriteLineAsync(
				$"{field.Key} is a backend password — it must be the real password (sealed enc:v1: or plaintext), " +
				"not a pbkdf2$ hash the backend cannot verify against.");
			return null;
		}

		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out _);
		if (key is null)
		{
			await Console.Error.WriteLineAsync(
				"Warning: no Encryption key configured — the backend password is stored in PLAINTEXT. " +
				"Prefer: echo -n '...' | eas user secret <login> " + field.Key);
			return raw;
		}

		await Console.Error.WriteLineAsync(
			"Warning: plaintext password on the command line (visible in shell history/ps) — " +
			$"prefer: echo -n '...' | eas user secret <login> {field.Key}. Stored sealed (enc:v1:).");
		string sealedValue = SecretValue.Seal(raw, key);
		System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
		return sealedValue;
	}
}

internal sealed class UserUnsetCommand(IAnsiConsole terminal) : UserCommandBase<UserUnsetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }

		[CommandArgument(1, "<key>")]
		public required string Key { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		AccountFieldPaths.FieldPath? field = AccountFieldPaths.Find(settings.Key);
		if (field is null)
		{
			await Console.Error.WriteLineAsync(
				$"Unknown field '{settings.Key}'. Valid fields: {string.Join(", ", AccountFieldPaths.Keys)}");
			return 1;
		}

		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		field.Set(entry, null);
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}

internal sealed class UserPasswordCommand(IAnsiConsole terminal)
	: UserCommandBase<UserPasswordCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		string password = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
		if (password.Length == 0)
		{
			await Console.Error.WriteLineAsync("Usage: echo -n 'password' | eas user password <login>");
			return 1;
		}

		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		entry.Password = GatewayPasswordHasher.Hash(password);
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}

internal sealed class UserSecretCommand(IAnsiConsole terminal)
	: UserCommandBase<UserSecretCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }

		[CommandArgument(1, "<key>")]
		[Description("A per-role backend password, e.g. Backends:MailStore:Password.")]
		public required string Key { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		AccountFieldPaths.FieldPath? field = AccountFieldPaths.Find(settings.Key);
		if (field is null || !field.IsSecret || !field.Key.Contains(':'))
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Key}' is not a backend password field. " +
				$"Valid: {string.Join(", ", AccountFieldPaths.BackendSecretKeys)}");
			return 1;
		}

		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? keyError);
		if (key is null)
		{
			await Console.Error.WriteLineAsync(keyError
				?? "Sealing requires the ActiveSync:Encryption master key (present in a running pod).");
			return 1;
		}

		string secret = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
		if (secret.Length == 0)
		{
			await Console.Error.WriteLineAsync("Usage: echo -n 'backend-password' | eas user secret <login> <key>");
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
			return 1;
		}

		string sealedValue = SecretValue.Seal(secret, key);
		System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		field.Set(entry, sealedValue);
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}
