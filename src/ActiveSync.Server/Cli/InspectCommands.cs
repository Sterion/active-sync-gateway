using System.ComponentModel;
using System.Globalization;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
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
		AccountStore store = services.GetRequiredService<AccountStore>();
		ActiveSyncOptions options = services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value;
		List<(string UserName, AccountOptions Options, DateTime UpdatedUtc)> dbEntries =
			await store.ListAsync(cancellationToken);
		Dictionary<string, AccountOptions> configUsers =
			options.Users ?? new Dictionary<string, AccountOptions>(StringComparer.OrdinalIgnoreCase);

		// State side: usage aggregates grouped by login (the former `eas users`).
		var deviceStats = await db.Devices
			.GroupBy(d => d.UserName)
			.Select(g => new { User = g.Key, Count = g.Count(), LastSeen = g.Max(d => d.LastSeenUtc) })
			.ToListAsync(cancellationToken);
		var folderStats = await db.UserFolders
			.Where(f => !f.Deleted)
			.GroupBy(f => f.UserName)
			.Select(g => new { User = g.Key, Count = g.Count() })
			.ToListAsync(cancellationToken);
		var itemStats = await db.LocalItems
			.GroupBy(i => new { i.UserName, i.Collection })
			.Select(g => new { g.Key.UserName, g.Key.Collection, Count = g.Count() })
			.ToListAsync(cancellationToken);
		List<LoginBlock> blocks = await db.LoginBlocks.ToListAsync(cancellationToken);

		// Full outer join: everyone declared OR seen in the state database.
		SortedSet<string> users = new(StringComparer.OrdinalIgnoreCase);
		users.UnionWith(configUsers.Keys);
		users.UnionWith(dbEntries.Select(e => e.UserName));
		users.UnionWith(deviceStats.Select(s => s.User));
		users.UnionWith(folderStats.Select(s => s.User));
		users.UnionWith(itemStats.Select(s => s.UserName));
		users.UnionWith(blocks.Select(b => b.UserName));
		if (users.Count == 0)
		{
			Terminal.WriteLine("No users are declared or have any state.");
			return 0;
		}

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("User", "Origin", "Mail", "Admin", "Gateway pw", "Overrides",
			"Devices", "Last seen (UTC)", "Folders", "Contacts", "Calendar", "Tasks", "Notes", "Blocked");
		foreach (string user in users)
		{
			// Declared attributes — null when the login only has state (a pass-through user).
			bool inDb = dbEntries.Any(e => string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase));
			bool inConfig = configUsers.ContainsKey(user);
			AccountOptions? declared = inDb
				? dbEntries.First(e => string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase)).Options
				: inConfig ? configUsers[user] : null;
			string origin = declared is null
				? "pass-through"
				: inDb
					? declared.AutoProvisioned == true ? "db (auto)" : inConfig ? "db (shadows config)" : "db"
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
			var devices = deviceStats.FirstOrDefault(s => s.User == user);
			int folders = folderStats.FirstOrDefault(s => s.User == user)?.Count ?? 0;
			int ItemCount(string collection) =>
				itemStats.FirstOrDefault(s => s.UserName == user && s.Collection == collection)?.Count ?? 0;
			int deviceBlocks = blocks.Count(b => b.UserName == user && b.DeviceId is not null);
			string blocked = declared?.Enabled == false
				? "disabled"
				: blocks.Any(b => b.UserName == user && b.DeviceId is null)
					? "yes"
					: deviceBlocks > 0
						? $"{deviceBlocks} device(s)"
						: "-";

			AddRow(table, user, origin, declared?.MailAddress ?? "-",
				declared?.Admin == true ? "yes" : "-", password,
				overrides.Count > 0 ? string.Join(", ", overrides) : "-",
				(devices?.Count ?? 0).ToString(),
				devices is null ? "-" : Utc(devices.LastSeen),
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
		IQueryable<Device> query = db.Devices;
		if (settings.User is not null)
			query = query.Where(d => d.UserName == settings.User);
		List<Device> devices = await query
			.OrderBy(d => d.UserName).ThenBy(d => d.DeviceId)
			.ToListAsync(cancellationToken);
		if (devices.Count == 0)
		{
			Terminal.WriteLine(settings.User is null
				? "No devices are registered."
				: $"No devices are registered for '{settings.User}'.");
			return 0;
		}

		List<LoginBlock> blocks = await db.LoginBlocks.ToListAsync(cancellationToken);

		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumns("User", "Device id", "Type", "Created (UTC)", "Last seen (UTC)", "Folder sync key", "Blocked");
		foreach (Device device in devices)
		{
			string blocked = blocks.Any(b => b.UserName == device.UserName && b.DeviceId is null)
				? "user"
				: blocks.Any(b => b.UserName == device.UserName && b.DeviceId == device.DeviceId)
					? "yes"
					: "-";
			AddRow(table, device.UserName, device.DeviceId,
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
		var folders = await db.UserFolders
			.Where(f => f.UserName == settings.User && !f.Deleted)
			.Select(f => new { f.Id, f.DisplayName, f.BackendKey, f.EasClass, DavItems = f.DavItems.Count })
			.OrderBy(f => f.BackendKey)
			.ToListAsync(cancellationToken);
		if (folders.Count == 0)
		{
			Terminal.WriteLine($"No folders are registered for '{settings.User}'.");
			return 0;
		}

		Dictionary<string, int> localCounts = await db.LocalItems
			.Where(i => i.UserName == settings.User)
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
		IQueryable<LocalItem> query = db.LocalItems.Where(i => i.UserName == settings.User);
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
		LocalItem? item = await db.LocalItems.FirstOrDefaultAsync(
			i => i.UserName == settings.User && i.Collection == settings.Collection && i.Uid == settings.Uid,
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
			await Console.Out.WriteLineAsync(protector.Unprotect(item.Content, item.UserName, item.Collection));
			return 0;
		}
		catch (BackendException ex)
		{
			await Console.Error.WriteLineAsync($"Cannot decrypt the item: {ex.Message}");
			return 1;
		}
	}
}
