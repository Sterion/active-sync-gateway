using System.ComponentModel;
using System.Globalization;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Base for commands that query the state database: builds the CLI service provider (with
///   the friendly config/migration errors) and hands a scoped context to the command body.
/// </summary>
internal abstract class DatabaseCommand<TSettings>(IAnsiConsole terminal) : AsyncCommand<TSettings>
	where TSettings : CommandSettings
{
	/// <summary>Injected so CommandAppTester captures output; production resolves the real console.</summary>
	protected IAnsiConsole Terminal { get; } = terminal;

	protected sealed override async Task<int> ExecuteAsync(
		CommandContext context, TSettings settings, CancellationToken cancellationToken)
	{
		// When this command is forwarded to the warm gateway, reuse the host's already-built and
		// already-migrated provider instead of constructing a parallel container (and probing pending
		// migrations) per invocation. Standalone (no gateway answered) falls back to CliServices.
		if (CliHostServices.Current is { } host)
		{
			await using AsyncServiceScope hostScope = host.CreateAsyncScope();
			SyncDbContext hostDb = hostScope.ServiceProvider.GetRequiredService<SyncDbContext>();
			return await RunAsync(hostScope.ServiceProvider, hostDb, settings, cancellationToken);
		}

		ServiceProvider? services = await CliServices.TryCreateAsync();
		if (services is null)
			return 1;
		await using ServiceProvider _ = services;
		await using AsyncServiceScope scope = services.CreateAsyncScope();
		SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
		return await RunAsync(scope.ServiceProvider, db, settings, cancellationToken);
	}

	protected abstract Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, TSettings settings, CancellationToken cancellationToken);

	protected static string Utc(DateTime? value)
		=> value is null ? "-" : value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

	/// <summary>Plain-text row cells — Text, not markup, so user-supplied strings render verbatim.</summary>
	protected static void AddRow(Table table, params string[] cells)
		=> table.AddRow(cells.Select(c => new Text(c)).ToArray());
}

/// <summary>
///   The single user overview: every DECLARED account (config ⊕ database — origin, mail, admin,
///   gateway password, per-role overrides) full-outer-joined with each login's STATE-database usage
///   (devices, last seen, folders, local item counts, blocks) on login == user name. A login may
///   appear declared-only (just provisioned, no sync yet), state-only (a pass-through user who has
///   never been declared) or both. This is the merge of the former `eas users` and `eas user list`.
/// </summary>
internal sealed class UsersCommand(IAnsiConsole terminal) : DatabaseCommand<UsersCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings;

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		// Declared side: config overlay ⊕ database rows (the former `eas user list`).
		UserStore store = services.GetRequiredService<UserStore>();
		// IOptionsMonitor, not a captured IOptions — see UserCommandBase.RunAsync's note.
		ActiveSyncOptions options = services.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>().CurrentValue;
		List<(string UserName, UserOptions Options, DateTime UpdatedUtc, bool Valid)> dbEntries =
			await store.ListAsync(cancellationToken);
		Dictionary<string, UserOptions> configUsers =
			options.Users ?? new Dictionary<string, UserOptions>(StringComparer.OrdinalIgnoreCase);

		// State side: usage aggregates grouped by login (the former `eas users`).
		var deviceStats = await db.Devices
			.GroupBy(d => d.User.Login)
			.Select(g => new { User = g.Key, Count = g.Count(), LastSeen = g.Max(d => d.LastSeenUtc) })
			.ToListAsync(cancellationToken);
		var folderStats = await db.UserFolders
			.Where(f => !f.Deleted)
			.GroupBy(f => f.User.Login)
			.Select(g => new { User = g.Key, Count = g.Count() })
			.ToListAsync(cancellationToken);
		var itemStats = await db.LocalItems
			.GroupBy(i => new { i.User.Login, i.Collection })
			.Select(g => new { UserName = g.Key.Login, g.Key.Collection, Count = g.Count() })
			.ToListAsync(cancellationToken);
		var blocks = await db.LoginBlocks
			.Select(b => new { Login = b.Device.User.Login, b.Device.DeviceId })
			.ToListAsync(cancellationToken);

		// Full outer join: everyone declared OR seen in the state database.
		SortedSet<string> users = new(StringComparer.OrdinalIgnoreCase);
		users.UnionWith(configUsers.Keys);
		users.UnionWith(dbEntries.Select(e => e.UserName));
		users.UnionWith(deviceStats.Select(s => s.User));
		users.UnionWith(folderStats.Select(s => s.User));
		users.UnionWith(itemStats.Select(s => s.UserName));
		users.UnionWith(blocks.Select(b => b.Login));
		if (users.Count == 0)
		{
			Terminal.WriteLine("No users are declared or have any state.");
			return 0;
		}

		// Project each per-login list into an OrdinalIgnoreCase lookup ONCE, so the render loop is
		// O(users) point-reads instead of O(users × rows) repeated FirstOrDefault/Any/Count scans (tens
		// of millions of comparisons on a large fleet). Case-insensitive keys also line up state rows
		// recorded under a casing that differs from the login — the old ordinal `==` scans missed those.
		Dictionary<string, (UserOptions Options, bool Valid)> dbByUser = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string userName, UserOptions options_, DateTime _, bool valid) in dbEntries)
			dbByUser.TryAdd(userName, (options_, valid));
		Dictionary<string, (int Count, DateTime LastSeen)> devicesByUser = deviceStats
			.GroupBy(s => s.User, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => (g.Sum(s => s.Count), g.Max(s => s.LastSeen)),
				StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> foldersByUser = folderStats
			.GroupBy(s => s.User, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.Sum(s => s.Count), StringComparer.OrdinalIgnoreCase);
		ILookup<string, (string Collection, int Count)> itemsByUser = itemStats
			.ToLookup(s => s.UserName, s => (s.Collection, s.Count), StringComparer.OrdinalIgnoreCase);
		// One entry per BLOCKED DEVICE, grouped by its owner's login.
		ILookup<string, string> blocksByUser =
			blocks.ToLookup(b => b.Login, b => b.DeviceId, StringComparer.OrdinalIgnoreCase);

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("User", "Origin", "Mail", "Admin", "Gateway pw", "Overrides",
			"Devices", "Last seen (UTC)", "Folders", "Contacts", "Calendar", "Tasks", "Notes", "Blocked");
		foreach (string user in users)
		{
			// Declared attributes — null when the login only has state (a pass-through user).
			bool inDb = dbByUser.TryGetValue(user, out (UserOptions Options, bool Valid) dbEntry);
			bool inConfig = configUsers.ContainsKey(user);
			UserOptions? declared = inDb ? dbEntry.Options : inConfig ? configUsers[user] : null;
			// A row whose JSON does not parse is surfaced FLAGGED, not omitted, so the
			// operator can see (and fix) the login the auth path is silently ignoring.
			string origin = declared is null
				? "pass-through"
				: inDb
					? !dbEntry.Valid ? "db (INVALID)"
						: declared.AutoProvisioned == true ? "db (auto)" : inConfig ? "db (shadows config)" : "db"
					: "config";
			string password = string.IsNullOrWhiteSpace(declared?.Password)
				? "-"
				: GatewayPasswordHasher.IsHashed(declared.Password) ? "***(pbkdf2)" : "***(PLAINTEXT)";
			List<string> overrides = [];
			foreach ((string roleName, BackendRoleOverride roleOverride) in
			         (declared?.Backends ?? []).OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
				overrides.Add(roleOverride.Enabled == false
					? $"{roleName.ToLowerInvariant()}=off"
					: roleOverride.Provider is { } switched
						? $"{roleName.ToLowerInvariant()}={switched}"
						: roleName.ToLowerInvariant());

			// State attributes.
			bool hasDevices = devicesByUser.TryGetValue(user, out (int Count, DateTime LastSeen) devices);
			int folders = foldersByUser.GetValueOrDefault(user);
			int ItemCount(string collection) =>
				itemsByUser[user].Where(i => i.Collection == collection).Sum(i => i.Count);
			// Two distinct mechanisms, reported distinctly: the USER is disabled, or N of their
			// DEVICES are blocked (blocks are per-device only — decision 19).
			int deviceBlocks = blocksByUser[user].Count();
			string blocked = declared?.Enabled == false
				? "disabled"
				: deviceBlocks > 0
					? $"{deviceBlocks} device(s)"
					: "-";

			AddRow(table, user, origin, declared?.MailAddress ?? "-",
				declared?.Admin == true ? "yes" : "-", password,
				overrides.Count > 0 ? string.Join(", ", overrides) : "-",
				(hasDevices ? devices.Count : 0).ToString(),
				hasDevices ? Utc(devices.LastSeen) : "-",
				folders.ToString(),
				ItemCount("contacts").ToString(),
				ItemCount("calendar").ToString(),
				ItemCount("tasks").ToString(),
				ItemCount("notes").ToString(),
				blocked);
		}

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class DevicesCommand(IAnsiConsole terminal) : DatabaseCommand<DevicesCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "[user]")]
		[Description("Only list devices belonging to this user.")]
		public string? User { get; init; }
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		DeviceAdminService admin = services.GetRequiredService<DeviceAdminService>();
		DeviceAdminService.DevicePage page = await admin.ListAsync(settings.User, 0, null, cancellationToken);
		if (page.Devices.Count == 0)
		{
			Terminal.WriteLine(settings.User is null
				? "No devices are registered."
				: $"No devices are registered for '{settings.User}'.");
			return 0;
		}

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("User", "Device id", "Type", "Created (UTC)", "Last seen (UTC)", "Folder sync key", "Blocked");
		foreach (DeviceAdminService.DeviceListing listing in page.Devices)
		{
			Device device = listing.Device;
			string blocked = listing.UserDisabled ? "user disabled" : listing.Blocked ? "yes" : "-";
			AddRow(table, listing.Login, device.DeviceId,
				device.DeviceType.Length > 0 ? device.DeviceType : "-",
				Utc(device.CreatedUtc), Utc(device.LastSeenUtc),
				device.FolderSyncKey.ToString(),
				blocked);
		}

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class FoldersCommand(IAnsiConsole terminal) : DatabaseCommand<FoldersCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<user>")]
		[Description("The user whose folder registry to list.")]
		public required string User { get; init; }
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		string normalizedFolderLogin = UserStore.NormalizeLogin(settings.User);
		var folders = await db.UserFolders
			.Where(f => f.User.Login == normalizedFolderLogin && !f.Deleted)
			.Select(f => new { f.Id, f.DisplayName, f.BackendKey, f.EasClass, DavItems = f.DavItems.Count })
			.OrderBy(f => f.BackendKey)
			.ToListAsync(cancellationToken);
		if (folders.Count == 0)
		{
			Terminal.WriteLine($"No folders are registered for '{settings.User}'.");
			return 0;
		}

		Dictionary<string, int> localCounts = await db.LocalItems
			.Where(i => i.User.Login == normalizedFolderLogin)
			.GroupBy(i => i.Collection)
			.Select(g => new { Collection = g.Key, Count = g.Count() })
			.ToDictionaryAsync(g => g.Collection, g => g.Count, cancellationToken);

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("Id", "Name", "Backend key", "Class", "Items");
		foreach (var folder in folders)
		{
			// Mail lives on the IMAP server; only DAV mappings and local-store rows are counted here.
			string items = folder.BackendKey.StartsWith("local:", StringComparison.Ordinal)
				? localCounts.GetValueOrDefault(folder.BackendKey["local:".Length..]).ToString()
				: folder.DavItems > 0
					? folder.DavItems.ToString()
					: "-";
			AddRow(table, folder.Id.ToString(), folder.DisplayName, folder.BackendKey, folder.EasClass, items);
		}

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class ItemsCommand(IAnsiConsole terminal) : DatabaseCommand<ItemsCommand.Settings>(terminal)
{
	private static readonly string[] Collections = ["contacts", "calendar", "tasks", "notes"];

	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<user>")]
		[Description("The user whose local items to list.")]
		public required string User { get; init; }

		[CommandArgument(1, "[collection]")]
		[Description("Limit to one collection: contacts, calendar, tasks or notes.")]
		public string? Collection { get; init; }

		public override ValidationResult Validate()
			=> Collection is null || Collections.Contains(Collection)
				? ValidationResult.Success()
				: ValidationResult.Error($"Unknown collection '{Collection}' (use {string.Join(", ", Collections)}).");
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		string normalizedItemsLogin = UserStore.NormalizeLogin(settings.User);
		IQueryable<LocalItem> query = db.LocalItems.Where(i => i.User.Login == normalizedItemsLogin);
		if (settings.Collection is not null)
			query = query.Where(i => i.Collection == settings.Collection);
		var items = await query
			.OrderBy(i => i.Collection).ThenBy(i => i.Uid)
			.Select(i => new { i.Collection, i.Uid, i.Version, i.ItemDateUtc, i.LastModifiedUtc })
			.ToListAsync(cancellationToken);
		if (items.Count == 0)
		{
			Terminal.WriteLine($"No local items for '{settings.User}'"
				+ (settings.Collection is null ? "." : $" in '{settings.Collection}'."));
			return 0;
		}

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("Collection", "Uid", "Version", "Item date (UTC)", "Modified (UTC)");
		foreach (var item in items)
			AddRow(table, item.Collection, item.Uid, item.Version.ToString(), Utc(item.ItemDateUtc), Utc(item.LastModifiedUtc));

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class ShowCommand(IAnsiConsole terminal) : DatabaseCommand<ShowCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<user>")]
		public required string User { get; init; }

		[CommandArgument(1, "<collection>")]
		[Description("contacts, calendar, tasks or notes.")]
		public required string Collection { get; init; }

		[CommandArgument(2, "<uid>")]
		public required string Uid { get; init; }
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		string normalizedShowLogin = UserStore.NormalizeLogin(settings.User);
		LocalItem? item = await db.LocalItems.FirstOrDefaultAsync(
			i => i.User.Login == normalizedShowLogin && i.Collection == settings.Collection && i.Uid == settings.Uid,
			cancellationToken);
		if (item is null)
		{
			await Console.Error.WriteLineAsync(
				$"No item '{settings.Uid}' in '{settings.Collection}' for '{settings.User}'.");
			return 1;
		}

		LocalContentProtector protector = services.GetRequiredService<LocalContentProtector>();
		try
		{
			// Raw content to stdout (pipe-friendly); errors and tables never mix into it.
			await Console.Out.WriteLineAsync(protector.Unprotect(item.Content, item.UserId, item.Collection));
			return 0;
		}
		catch (BackendException ex)
		{
			await Console.Error.WriteLineAsync($"Cannot decrypt the item: {ex.Message}");
			return 1;
		}
	}
}
