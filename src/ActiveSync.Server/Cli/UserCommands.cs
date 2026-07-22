using System.ComponentModel;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
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

	protected static AccountOptions Clone(AccountOptions source) => AccountEditing.Clone(source);

	/// <summary>DB entry, else a copy of the config entry, else a fresh one.</summary>
	protected static Task<AccountOptions> LoadStartingEntryAsync(
		AccountStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		return AccountEditing.LoadStartingEntryAsync(store, options, login, ct);
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
		Terminal.WriteLine($"{login}  {StartupSummary.DescribeUser(new MergedAccount(entry, true, AccountEditing.FindConfigUser(options, login) is not null))}");
		Terminal.WriteLine(PickupNote(options));
		return 0;
	}

	protected static string PickupNote(ActiveSyncOptions options)
	{
		// A negative/non-finite cadence no longer disables live refresh — it is clamped to
		// "every request" (B11), so a running gateway always picks this up.
		double seconds = double.IsFinite(options.Auth.UsersRefreshSeconds)
			? Math.Max(options.Auth.UsersRefreshSeconds, 0)
			: 0;
		return $"A running gateway picks this up within ~{Math.Max(seconds, 1):0}s.";
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
		AccountOptions? fromConfig = AccountEditing.FindConfigUser(options, settings.Login);
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

internal sealed class UserDisableCommand(IAnsiConsole terminal) : UserCommandBase<UserDisableCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		[Description("The gateway login to disable.")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		entry.Enabled = false;
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}

internal sealed class UserEnableCommand(IAnsiConsole terminal) : UserCommandBase<UserEnableCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		[Description("The gateway login to re-enable.")]
		public required string Login { get; init; }
	}

	protected override async Task<int> RunAsync(
		AccountStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		// Enabled is the default, so re-enabling clears the flag rather than storing an explicit true.
		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		entry.Enabled = null;
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}
}

internal sealed class UserSetCommand(IAnsiConsole terminal) : UserCommandBase<UserSetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }

		[CommandArgument(1, "<key>")]
		[Description("Field path, e.g. MailAddress, Backends:MailStore:Settings:Host, Backends:Contacts:Enabled.")]
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
	///   Password keys on argv, via the shared <see cref="AccountSecretPolicy" /> (same rules
	///   as the web API): an already-prepared value (pbkdf2$/enc:v1:) is stored as-is; plaintext
	///   is hashed (gateway Password) or sealed (backend passwords) with a shell-history
	///   warning — the stdin commands ('user password'/'user secret') keep secrets out of argv.
	/// </summary>
	private static async Task<string?> PrepareSecretAsync(
		AccountFieldPaths.FieldPath field, string raw, ActiveSyncOptions options)
	{
		AccountSecretPolicy.SecretResult result = field.IsGatewayPassword
			? AccountSecretPolicy.PrepareGatewayPassword(raw)
			: AccountSecretPolicy.PrepareBackendPassword(raw, options.Encryption, field.Key);
		if (result.Error is not null)
		{
			await Console.Error.WriteLineAsync(result.Error);
			return null;
		}

		string? warning = result.Plaintext switch
		{
			AccountSecretPolicy.PlaintextDisposition.Hashed =>
				"Warning: plaintext password on the command line (visible in shell history/ps) — " +
				"prefer: echo -n '...' | eas user password <login>. Stored as a pbkdf2$ hash.",
			AccountSecretPolicy.PlaintextDisposition.Sealed =>
				"Warning: plaintext password on the command line (visible in shell history/ps) — " +
				$"prefer: echo -n '...' | eas user secret <login> {field.Key}. Stored sealed (enc:v1:).",
			AccountSecretPolicy.PlaintextDisposition.StoredPlaintext =>
				"Warning: no Encryption key configured — the backend password is stored in PLAINTEXT. " +
				$"Prefer: echo -n '...' | eas user secret <login> {field.Key}",
			_ => null
		};
		if (warning is not null)
			await Console.Error.WriteLineAsync(warning);
		return result.Value;
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

		// C6: through the shared policy (strength floor + empty/sealed rejection), not a direct hash.
		AccountSecretPolicy.SecretResult prepared = AccountSecretPolicy.PrepareGatewayPassword(password);
		if (prepared.Error is not null)
		{
			await Console.Error.WriteLineAsync(prepared.Error);
			return 1;
		}

		AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		entry.Password = prepared.Value;
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
		if (field is null || !field.IsSecret || field.IsGatewayPassword)
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

		// Zero the master key on every exit — including a failed/cancelled stdin read or a throwing
		// seal. This runs inside the long-lived gateway process (via /cli), so a leaked key array
		// sits on the heap until GC (L42).
		try
		{
			string secret = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
			if (secret.Length == 0)
			{
				await Console.Error.WriteLineAsync("Usage: echo -n 'backend-password' | eas user secret <login> <key>");
				return 1;
			}

			string sealedValue = SecretValue.Seal(secret, key);
			AccountOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
			field.Set(entry, sealedValue);
			return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
		}
	}
}
