using System.ComponentModel;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

internal class ShareGrantSettings : CommandSettings
{
	[CommandArgument(0, "<user>")]
	[Description("The gateway login the grant applies to.")]
	public required string User { get; init; }

	[CommandArgument(1, "<collectionHref>")]
	[Description("CalDAV collection path, e.g. /dav/cal/family/ (must live on the user's CalDAV server).")]
	public required string CollectionHref { get; init; }
}

internal sealed class ShareAddSettings : ShareGrantSettings
{
	[CommandOption("--read-only")]
	[Description("Enforce read-only on the gateway: client edits in this calendar are silently reverted.")]
	public bool ReadOnly { get; init; }
}

/// <summary>Grant (or re-mode) one extra CalDAV collection as a calendar folder for a user.</summary>
internal sealed class ShareAddCommand(IAnsiConsole terminal) : DatabaseCommand<ShareAddSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, ShareAddSettings settings, CancellationToken cancellationToken)
	{
		string href = settings.CollectionHref.Trim();
		if (!href.StartsWith('/'))
		{
			Terminal.MarkupLine("[red]The collection must be an absolute path starting with '/'.[/]");
			return 1;
		}

		SharedCalendarGrant? existing = await db.SharedCalendarGrants.FirstOrDefaultAsync(
			g => g.UserName == settings.User && g.CollectionHref == href, cancellationToken);
		string mode = settings.ReadOnly ? "read-only" : "read-write";
		if (existing is not null)
		{
			if (existing.ReadOnly == settings.ReadOnly)
			{
				Terminal.WriteLine($"'{settings.User}' already has {href} ({mode}).");
				return 0;
			}

			existing.ReadOnly = settings.ReadOnly;
			await db.SaveChangesAsync(cancellationToken);
			Terminal.WriteLine($"Changed {href} for '{settings.User}' to {mode}.");
			return 0;
		}

		// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
		// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
		db.SharedCalendarGrants.Add(new SharedCalendarGrant
		{
			UserName = settings.User,
			CollectionHref = href,
			ReadOnly = settings.ReadOnly,
			CreatedUtc = DateTime.UtcNow,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(cancellationToken);
		Terminal.WriteLine(
			$"Granted {href} to '{settings.User}' ({mode}). " +
			"Applies when the user's backend session is next built (idle recycle or restart).");
		return 0;
	}
}

internal sealed class ShareRemoveCommand(IAnsiConsole terminal) : DatabaseCommand<ShareGrantSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, ShareGrantSettings settings, CancellationToken cancellationToken)
	{
		string href = settings.CollectionHref.Trim();
		SharedCalendarGrant? existing = await db.SharedCalendarGrants.FirstOrDefaultAsync(
			g => g.UserName == settings.User && g.CollectionHref == href, cancellationToken);
		if (existing is null)
		{
			Terminal.WriteLine($"'{settings.User}' has no grant for {href} — nothing to remove.");
			return 0;
		}

		db.SharedCalendarGrants.Remove(existing);
		await db.SaveChangesAsync(cancellationToken);
		Terminal.WriteLine($"Removed {href} from '{settings.User}'. " +
		                   "The folder disappears when the user's backend session is next built.");
		return 0;
	}
}

internal sealed class ShareListSettings : CommandSettings
{
	[CommandArgument(0, "[user]")]
	[Description("Only list grants of this user.")]
	public string? User { get; init; }
}

internal sealed class ShareListCommand(IAnsiConsole terminal) : DatabaseCommand<ShareListSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, ShareListSettings settings, CancellationToken cancellationToken)
	{
		List<SharedCalendarGrant> grants = await db.SharedCalendarGrants.AsNoTracking()
			.Where(g => settings.User == null || g.UserName == settings.User)
			.OrderBy(g => g.UserName).ThenBy(g => g.CollectionHref)
			.ToListAsync(cancellationToken);
		if (grants.Count == 0)
		{
			Terminal.WriteLine(settings.User is null
				? "No shared-calendar grants."
				: $"No shared-calendar grants for '{settings.User}'.");
			return 0;
		}

		Table table = new Table().AddColumns("User", "Collection", "Mode", "Granted (UTC)");
		foreach (SharedCalendarGrant grant in grants)
			table.AddRow(
				Markup.Escape(grant.UserName),
				Markup.Escape(grant.CollectionHref),
				grant.ReadOnly ? "read-only" : "read-write",
				Utc(grant.CreatedUtc));
		Terminal.Write(table);
		return 0;
	}
}
