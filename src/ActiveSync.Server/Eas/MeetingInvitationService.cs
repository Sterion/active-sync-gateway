using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using MimeKit;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Outbound iMIP: mails METHOD:REQUEST/CANCEL when the user changes a meeting they
///   organize, filling the scheduling gap of DAV servers without RFC 6638. Every entry
///   point is best-effort — a mail failure logs a warning and never fails the Sync
///   command that triggered it (the calendar write already succeeded).
/// </summary>
public sealed class MeetingInvitationService(ILogger<MeetingInvitationService> logger)
{
	/// <summary>New event: invite every attendee when the user is the organizer.</summary>
	public async Task AfterCreateAsync(
		EasContext context, IContentStore store, string folderBackendKey, string itemKey, CancellationToken ct)
	{
		try
		{
			if (store is not ICalendarOperations calendar)
				return;
			string? ics = await calendar.GetRawEventAsync(folderBackendKey, itemKey, ct);
			CalendarConverter.SchedulingInfo? info = ics is null ? null : CalendarConverter.ReadSchedulingInfo(ics);
			if (info is null || !await ShouldActAsync(context, calendar, info, ct))
				return;
			await SendAsync(context, ImipMailBuilder.BuildRequest(
				ics!, info.Organizer!, Recipients(info, context), $"Invitation: {info.Summary}"), ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Invitation mail after event create failed (event stored fine)");
		}
	}

	/// <summary>
	///   Updated event: CANCEL to removed attendees; REQUEST to everyone on a
	///   scheduling-significant change, or only to newly added attendees otherwise.
	///   A diff with no significant change and no membership change sends NOTHING —
	///   that guard is what keeps reminder edits and ghosted Changes silent.
	/// </summary>
	public async Task AfterChangeAsync(
		EasContext context, IContentStore store, string folderBackendKey, string itemKey,
		string? previousIcs, CancellationToken ct)
	{
		try
		{
			if (store is not ICalendarOperations calendar)
				return;
			string? ics = await calendar.GetRawEventAsync(folderBackendKey, itemKey, ct);
			CalendarConverter.SchedulingInfo? info = ics is null ? null : CalendarConverter.ReadSchedulingInfo(ics);
			if (info is null || !await ShouldActAsync(context, calendar, info, ct))
				return;

			CalendarConverter.SchedulingInfo? previous =
				previousIcs is null ? null : CalendarConverter.ReadSchedulingInfo(previousIcs);
			List<(string Email, string? Name)> current = Recipients(info, context);
			// E24: compute the previous recipient list ONCE. The old code called Recipients(previous)
			// inside both the removed- and added-filters, so it re-parsed and re-filtered the whole
			// attendee list per current attendee — O(n²) for a large meeting.
			List<(string Email, string? Name)> previousRecipients =
				previous is null ? [] : Recipients(previous, context);
			(List<(string Email, string? Name)> added, List<(string Email, string? Name)> removed) =
				DiffRecipients(previousRecipients, current, previousKnown: previous is not null);

			if (removed.Count > 0)
				await SendAsync(context, ImipMailBuilder.BuildCancel(
					info.Uid, info.Sequence + 1, info.Organizer!, removed, null,
					$"Cancelled: {info.Summary}"), ct);

			if (CalendarConverter.SchedulingSignificantlyDiffers(previousIcs, ics!))
			{
				if (current.Count > 0)
					await SendAsync(context, ImipMailBuilder.BuildRequest(
						ics!, info.Organizer!, current, $"Updated invitation: {info.Summary}"), ct);
			}
			else if (added.Count > 0)
			{
				await SendAsync(context, ImipMailBuilder.BuildRequest(
					ics!, info.Organizer!, added, $"Invitation: {info.Summary}"), ct);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Invitation mail after event change failed (event stored fine)");
		}
	}

	/// <summary>Single-occurrence cancel (16.x InstanceId delete): targeted RECURRENCE-ID CANCEL.</summary>
	public async Task AfterOccurrenceCancelAsync(
		EasContext context, IContentStore store, string folderBackendKey, string itemKey,
		DateTime occurrenceUtc, CancellationToken ct)
	{
		try
		{
			if (store is not ICalendarOperations calendar)
				return;
			string? ics = await calendar.GetRawEventAsync(folderBackendKey, itemKey, ct);
			CalendarConverter.SchedulingInfo? info = ics is null ? null : CalendarConverter.ReadSchedulingInfo(ics);
			if (info is null || !await ShouldActAsync(context, calendar, info, ct))
				return;
			await SendAsync(context, ImipMailBuilder.BuildCancel(
				info.Uid, info.Sequence + 1, info.Organizer!, Recipients(info, context), occurrenceUtc,
				$"Cancelled occurrence: {info.Summary}"), ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Cancellation mail after occurrence delete failed (event stored fine)");
		}
	}

	/// <summary>Deleted event: CANCEL to all attendees (ICS captured before the delete).</summary>
	public async Task AfterDeleteAsync(
		EasContext context, IContentStore store, string? deletedIcs, CancellationToken ct)
	{
		try
		{
			if (store is not ICalendarOperations calendar || deletedIcs is null)
				return;
			CalendarConverter.SchedulingInfo? info = CalendarConverter.ReadSchedulingInfo(deletedIcs);
			if (info is null || !await ShouldActAsync(context, calendar, info, ct))
				return;
			await SendAsync(context, ImipMailBuilder.BuildCancel(
				info.Uid, info.Sequence + 1, info.Organizer!, Recipients(info, context), null,
				$"Cancelled: {info.Summary}"), ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Cancellation mail after event delete failed (event deleted fine)");
		}
	}

	/// <summary>The stored ICS of a calendar item, for delete/change hooks that need the "before".</summary>
	public static async Task<string?> CaptureIcsAsync(
		IContentStore store, string folderBackendKey, string itemKey, ILogger logger, CancellationToken ct)
	{
		if (store is not ICalendarOperations calendar)
			return null;
		try
		{
			return await calendar.GetRawEventAsync(folderBackendKey, itemKey, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// E34: degrade to "no previous state" so the command still succeeds — but say so. A
			// swallowed read here makes the change hook treat every attendee as newly added and
			// re-invite the whole meeting; silently doing that on a transient backend hiccup is a
			// spam vector the operator can't see.
			logger.LogWarning(ex,
				"Could not read the pre-change ICS for {ItemKey}; iMIP hooks will treat this as no " +
				"previous state (a change may re-invite all attendees)", itemKey);
			return null;
		}
	}

	/// <summary>Attendees exist, the acting user is the organizer, and the knob/probe allows.</summary>
	private static async Task<bool> ShouldActAsync(
		EasContext context, ICalendarOperations calendar, CalendarConverter.SchedulingInfo info,
		CancellationToken ct)
	{
		if (info.Attendees.Count == 0 || info.Organizer is null)
			return false;
		string identity = context.Session.MailAddress ?? context.Device.UserName;
		if (!MailboxEquals(info.Organizer, identity))
			return false;
		return await calendar.ShouldSendInvitationsAsync(ct);
	}

	/// <summary>
	///   Attendees added and removed between the previous and current recipient lists, matched by
	///   mailbox. When the previous state is unknown (no prior ICS captured), everyone is "added" and
	///   nobody is "removed" — the same fallback the change hook has always used.
	/// </summary>
	internal static (List<(string Email, string? Name)> Added, List<(string Email, string? Name)> Removed)
		DiffRecipients(
			IReadOnlyList<(string Email, string? Name)> previous,
			IReadOnlyList<(string Email, string? Name)> current,
			bool previousKnown)
	{
		if (!previousKnown)
			return (current.ToList(), []);

		List<(string Email, string? Name)> added = current
			.Where(c => !previous.Any(p => MailboxEquals(p.Email, c.Email)))
			.ToList();
		List<(string Email, string? Name)> removed = previous
			.Where(p => !current.Any(c => MailboxEquals(c.Email, p.Email)))
			.ToList();
		return (added, removed);
	}

	/// <summary>All attendees except the organizer themself.</summary>
	private static List<(string Email, string? Name)> Recipients(
		CalendarConverter.SchedulingInfo info, EasContext context)
	{
		string identity = context.Session.MailAddress ?? context.Device.UserName;
		return info.Attendees
			.Where(a => !MailboxEquals(a.Email, identity) &&
			            (info.Organizer is null || !MailboxEquals(a.Email, info.Organizer)))
			.ToList();
	}

	private async Task SendAsync(EasContext context, MimeMessage message, CancellationToken ct)
	{
		using MemoryStream output = new();
		await message.WriteToAsync(output, ct);
		await context.Session.MailSubmit.SendAsync(output.ToArray(), ct);
		logger.LogInformation("iMIP {Method} sent to {Count} recipient(s) for {User}",
			(message.Body as MimePart)?.ContentType.Parameters["method"] ?? "?",
			message.To.Count, context.Device.UserName);
	}

	private static bool MailboxEquals(string a, string b)
	{
		return a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
	}
}
