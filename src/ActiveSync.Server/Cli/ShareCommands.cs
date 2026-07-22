using System.ComponentModel;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.Extensions.DependencyInjection;
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
		// The same identifier rules the web admin and every other write path apply (C16) — the CLI
		// used to check only that the href started with '/'.
		if (AdminIdentifiers.LoginProblem(settings.User) is { } loginError)
		{
			Terminal.MarkupLine($"[red]{Markup.Escape(loginError)}[/]");
			return 1;
		}
		if (AdminIdentifiers.HrefProblem(settings.CollectionHref) is { } hrefError)
		{
			Terminal.MarkupLine($"[red]{Markup.Escape(hrefError)}[/]");
			return 1;
		}

		string href = settings.CollectionHref.Trim();
		string mode = settings.ReadOnly ? "read-only" : "read-write";
		ShareAdminService shares = services.GetRequiredService<ShareAdminService>();
		ShareAdminService.ShareUpsert result =
			await shares.AddOrUpdateAsync(settings.User, href, settings.ReadOnly, cancellationToken);

		Terminal.WriteLine(result.Kind switch
		{
			ShareAdminService.UpsertKind.Unchanged => $"'{settings.User}' already has {href} ({mode}).",
			ShareAdminService.UpsertKind.Remoded => $"Changed {href} for '{settings.User}' to {mode}.",
			_ => $"Granted {href} to '{settings.User}' ({mode}). " +
				"Applies when the user's backend session is next built (idle recycle or restart)."
		});
		return 0;
	}
}

internal sealed class ShareRemoveCommand(IAnsiConsole terminal) : DatabaseCommand<ShareGrantSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, ShareGrantSettings settings, CancellationToken cancellationToken)
	{
		string href = settings.CollectionHref.Trim();
		ShareAdminService shares = services.GetRequiredService<ShareAdminService>();
		if (!await shares.RemoveAsync(settings.User, href, cancellationToken))
		{
			Terminal.WriteLine($"'{settings.User}' has no grant for {href} — nothing to remove.");
			return 0;
		}

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
		ShareAdminService shares = services.GetRequiredService<ShareAdminService>();
		ShareAdminService.SharePage page = await shares.ListAsync(settings.User, 0, null, cancellationToken);
		if (page.Grants.Count == 0)
		{
			Terminal.WriteLine(settings.User is null
				? "No shared-calendar grants."
				: $"No shared-calendar grants for '{settings.User}'.");
			return 0;
		}

		Table table = new Table().AddColumns("User", "Collection", "Mode", "Granted (UTC)");
		foreach (SharedCalendarGrant grant in page.Grants)
			table.AddRow(
				Markup.Escape(grant.UserName),
				Markup.Escape(grant.CollectionHref),
				grant.ReadOnly ? "read-only" : "read-write",
				Utc(grant.CreatedUtc));
		Terminal.Write(table);
		return 0;
	}
}
