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

	/// <summary>The resolved provider for commands that need more than the store (e.g. the delete guard).</summary>
	protected IServiceProvider Services { get; private set; } = null!;

	protected sealed override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, TSettings settings, CancellationToken cancellationToken)
	{
		Services = services;
		UserStore store = services.GetRequiredService<UserStore>();
		ActiveSyncOptions options = services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value;
		Roles = services.GetRequiredService<BackendRolesConfig>();
		Registry = services.GetRequiredService<BackendProviderRegistry>();
		return await RunAsync(store, options, settings, cancellationToken);
	}

	protected abstract Task<int> RunAsync(
		UserStore store, ActiveSyncOptions options, TSettings settings, CancellationToken cancellationToken);

	protected static UserOptions Clone(UserOptions source) => UserEditing.Clone(source);

	/// <summary>The database declaration, else a fresh one (never a copy of config — item 6).</summary>
	protected static Task<UserOptions> LoadStartingEntryAsync(
		UserStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		return UserEditing.LoadStartingEntryAsync(store, options, login, ct);
	}

	/// <summary>Validates, saves and reports; refuses invalid entries with config-grade messages.</summary>
	protected async Task<int> ValidateAndSaveAsync(
		UserStore store, ActiveSyncOptions options, string login, UserOptions entry, CancellationToken ct)
	{
		List<string> failures = UserResolver.ValidateEntry(options, Roles, Registry, login, entry);
		if (failures.Count > 0)
		{
			await Console.Error.WriteLineAsync("The entry would be invalid — nothing was saved:");
			foreach (string failure in failures)
				await Console.Error.WriteLineAsync($"  - {failure}");
			return 1;
		}

		await store.UpsertAsync(login, entry, ct);
		// Report the EFFECTIVE user, not just the row we wrote: a database declaration is a
		// per-field deviation now, so everything it does not set still comes from configuration
		// and the operator needs to see the result rather than the delta.
		UserOptions? configEntry = UserEditing.FindConfigUser(options, login);
		UserMerge.Merged effective = UserMerge.Merge(configEntry, entry);
		Terminal.WriteLine($"{login}  {StartupSummary.DescribeUser(
			new MergedUser(effective.Options, true, configEntry is not null, false, effective.Sources))}");
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		UserOptions? fromDb = await store.GetAsync(settings.Login, cancellationToken);
		UserOptions? fromConfig = UserEditing.FindConfigUser(options, settings.Login);
		if (fromDb is null && fromConfig is null)
		{
			await Console.Error.WriteLineAsync($"No declared user '{settings.Login}' (config or database).");
			return 1;
		}

		UserMerge.Merged resolved = UserMerge.Merge(fromConfig, fromDb);
		MergedUser effective = new(
			resolved.Options, fromDb is not null, fromDb is not null && fromConfig is not null,
			false, resolved.Sources);
		Terminal.WriteLine($"{settings.Login}  {StartupSummary.DescribeUser(effective)}");
		if (effective is { FromDatabase: true, ShadowsConfig: true })
			Terminal.WriteLine(
				"This login is declared in BOTH configuration and the database; the values above are " +
				"the per-field merge (database wins per field). 'eas user remove' drops the database " +
				"deviations and leaves the config entry.");
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		if (await store.GetAsync(settings.Login, cancellationToken) is not null)
		{
			await Console.Error.WriteLineAsync(
				$"A database entry for '{settings.Login}' already exists — use 'eas user set/show'.");
			return 1;
		}

		// A brand-new entry is an empty overlay (an allowlist grant); when a config entry
		// exists it is copied so the database version starts as an exact replacement.
		UserOptions entry = options.Users?.GetValueOrDefault(settings.Login) is { } fromConfig
			? Clone(fromConfig)
			: new UserOptions();
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
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

/// <summary>
///   Renames a user's login. Possible at all only because the identity is a surrogate key: the
///   rename is a single-row update, so sync state stays attached and locally-stored items stay
///   readable. The holder just updates the username on their phone.
/// </summary>
internal sealed class UserRenameCommand(IAnsiConsole terminal) : UserCommandBase<UserRenameCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		[Description("The current login.")]
		public required string Login { get; init; }

		[CommandArgument(1, "<newLogin>")]
		[Description("The login the phone will present from now on.")]
		public required string NewLogin { get; init; }
	}

	protected override async Task<int> RunAsync(
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		// Guard 1: a login is IMMUTABLE while configuration declares it. That is exactly what
		// makes matching config to database BY LOGIN safe — the database side can never drift,
		// because the only mutable side is the one configuration does not own.
		if (UserEditing.FindConfigUser(options, settings.Login) is not null)
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Login}' is declared in configuration — change it there (ActiveSync:Users), " +
				"not here. A config-declared login is immutable so the two sides cannot drift.");
			return 1;
		}

		// Guard 2: the new login must be free — including of a config entry, which would other-
		// wise start shadowing this user the moment configuration is next read.
		List<string> failures = new();
		UserResolver.ValidateLogin(settings.NewLogin, failures);
		if (failures.Count > 0)
		{
			foreach (string failure in failures)
				await Console.Error.WriteLineAsync(failure);
			return 1;
		}

		if (UserEditing.FindConfigUser(options, settings.NewLogin) is not null)
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.NewLogin}' is already declared in configuration — pick a login that is free.");
			return 1;
		}

		UserStore.RenameOutcome outcome =
			await store.RenameAsync(settings.Login, settings.NewLogin, cancellationToken);
		switch (outcome)
		{
			case UserStore.RenameOutcome.UnknownUser:
				await Console.Error.WriteLineAsync($"No user '{settings.Login}'.");
				return 1;
			case UserStore.RenameOutcome.Collision:
				await Console.Error.WriteLineAsync(
					$"'{settings.NewLogin}' is already taken by another user.");
				return 1;
			default:
				Terminal.WriteLine(
					$"Renamed '{settings.Login}' to '{settings.NewLogin}'. Sync state, devices and " +
					"locally-stored items are unaffected — the phone just needs its username updated.");
				Terminal.WriteLine(PickupNote(options));
				return 0;
		}
	}
}

/// <summary>
///   Deletes a user outright: the identity and everything the database cascades from it. Unlike
///   `user remove` (which drops the DATABASE DECLARATION and falls back to configuration), this
///   destroys data — locally-stored contacts, calendar, tasks and notes included, which in a
///   local-stores deployment exist nowhere else. Confirm-and-cascade: the impact is counted
///   first and the question names what is lost.
/// </summary>
internal sealed class UserDeleteCommand(IAnsiConsole terminal) : UserCommandBase<UserDeleteCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<login>")]
		public required string Login { get; init; }

		[CommandOption("-y|--yes")]
		[Description("Skip the confirmation prompt (required when not running interactively).")]
		public bool Yes { get; init; }
	}

	protected override async Task<int> RunAsync(
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		if (await store.FindUserIdAsync(settings.Login, cancellationToken) is null)
		{
			await Console.Error.WriteLineAsync($"No user '{settings.Login}'.");
			return 1;
		}

		DeviceAdminService devices = Services.GetRequiredService<DeviceAdminService>();
		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync(settings.Login, null, cancellationToken);

		if (!settings.Yes)
		{
			// Graduated friction: sync state alone rebuilds on the next sync and does not deserve
			// a dire warning; real content does, named exactly.
			string question = impact.DestroysContent
				? $"Permanently delete user '{settings.Login}'? This destroys {impact.DescribeContent()} " +
				  "which exist nowhere else."
				: $"Permanently delete user '{settings.Login}' and all of its gateway state?";

			if (CliConfirmation.CanAsk)
			{
				CliConfirmation.Ask(question);
				return 1;
			}

			if (!Terminal.Profile.Capabilities.Interactive)
			{
				await Console.Error.WriteLineAsync(
					$"{question}\nThis permanently deletes data; confirm with --yes when running non-interactively.");
				return 1;
			}

			if (!await Terminal.ConfirmAsync(question, false, cancellationToken))
			{
				Terminal.WriteLine("Aborted; nothing was deleted.");
				return 1;
			}
		}
		else if (impact.DestroysContent)
		{
			// Re-check on the confirmed call: the operator agreed to a SPECIFIC loss, and this is
			// a re-execution rather than a resumed transaction.
			Terminal.WriteLine($"Deleting {impact.DescribeContent()} along with the user.");
		}

		if (!await store.DeleteUserAsync(settings.Login, cancellationToken))
		{
			await Console.Error.WriteLineAsync($"No user '{settings.Login}'.");
			return 1;
		}

		Terminal.WriteLine($"Deleted user '{settings.Login}' and all of its gateway state.");
		if (UserEditing.FindConfigUser(options, settings.Login) is not null)
			Terminal.WriteLine(
				"NOTE: configuration still declares this login, so it will be re-created (empty) at the " +
				"next start or sign-in. Remove it from ActiveSync:Users to make the deletion final.");
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		// Enabled is the default, so re-enabling clears the flag rather than storing an explicit true.
		UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		UserFieldPaths.FieldPath? field = UserFieldPaths.Find(settings.Key);
		if (field is null)
		{
			await Console.Error.WriteLineAsync(
				$"Unknown field '{settings.Key}'. Valid fields: {string.Join(", ", UserFieldPaths.Keys)}");
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
		else if (!UserFieldPaths.TryParseValue(field, settings.Value, out value, out string? parseError))
		{
			await Console.Error.WriteLineAsync(parseError);
			return 1;
		}

		UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
		field.Set(entry, value);
		return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
	}

	/// <summary>
	///   Password keys on argv, via the shared <see cref="UserSecretPolicy" /> (same rules
	///   as the web API): an already-prepared value (pbkdf2$/enc:v1:) is stored as-is; plaintext
	///   is hashed (gateway Password) or sealed (backend passwords) with a shell-history
	///   warning — the stdin commands ('user password'/'user secret') keep secrets out of argv.
	/// </summary>
	private static async Task<string?> PrepareSecretAsync(
		UserFieldPaths.FieldPath field, string raw, ActiveSyncOptions options)
	{
		UserSecretPolicy.SecretResult result = field.IsGatewayPassword
			? UserSecretPolicy.PrepareGatewayPassword(raw)
			: UserSecretPolicy.PrepareBackendPassword(raw, options.Encryption, field.Key);
		if (result.Error is not null)
		{
			await Console.Error.WriteLineAsync(result.Error);
			return null;
		}

		string? warning = result.Plaintext switch
		{
			UserSecretPolicy.PlaintextDisposition.Hashed =>
				"Warning: plaintext password on the command line (visible in shell history/ps) — " +
				"prefer: echo -n '...' | eas user password <login>. Stored as a pbkdf2$ hash.",
			UserSecretPolicy.PlaintextDisposition.Sealed =>
				"Warning: plaintext password on the command line (visible in shell history/ps) — " +
				$"prefer: echo -n '...' | eas user secret <login> {field.Key}. Stored sealed (enc:v1:).",
			UserSecretPolicy.PlaintextDisposition.StoredPlaintext =>
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		UserFieldPaths.FieldPath? field = UserFieldPaths.Find(settings.Key);
		if (field is null)
		{
			await Console.Error.WriteLineAsync(
				$"Unknown field '{settings.Key}'. Valid fields: {string.Join(", ", UserFieldPaths.Keys)}");
			return 1;
		}

		UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		string password = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
		if (password.Length == 0)
		{
			await Console.Error.WriteLineAsync("Usage: echo -n 'password' | eas user password <login>");
			return 1;
		}

		// C6: through the shared policy (strength floor + empty/sealed rejection), not a direct hash.
		UserSecretPolicy.SecretResult prepared = UserSecretPolicy.PrepareGatewayPassword(password);
		if (prepared.Error is not null)
		{
			await Console.Error.WriteLineAsync(prepared.Error);
			return 1;
		}

		UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
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
		UserStore store, ActiveSyncOptions options, Settings settings, CancellationToken cancellationToken)
	{
		UserFieldPaths.FieldPath? field = UserFieldPaths.Find(settings.Key);
		if (field is null || !field.IsSecret || field.IsGatewayPassword)
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Key}' is not a backend password field. " +
				$"Valid: {string.Join(", ", UserFieldPaths.BackendSecretKeys)}");
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
			UserOptions entry = await LoadStartingEntryAsync(store, options, settings.Login, cancellationToken);
			field.Set(entry, sealedValue);
			return await ValidateAndSaveAsync(store, options, settings.Login, entry, cancellationToken);
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
		}
	}
}
