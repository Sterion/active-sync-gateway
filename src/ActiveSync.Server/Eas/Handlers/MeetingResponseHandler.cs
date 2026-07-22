using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>MeetingResponse (MS-ASCMD 2.2.1.11): accept/decline via CalDAV + iTIP reply mail.</summary>
public sealed class MeetingResponseHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<MeetingResponseHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace MR = EasNamespaces.MeetingResponse;

	public string Command => "MeetingResponse";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		List<XElement> results = new();

		foreach (XElement req in request?.Root?.Elements(MR + "Request") ?? [])
		{
			string? requestId = req.Element(MR + "RequestId")?.Value;
			string? collectionId = req.Element(MR + "CollectionId")?.Value;
			int userResponse = int.TryParse(req.Element(MR + "UserResponse")?.Value, out int ur) ? ur : 1;

			XElement Result(string status, string? calendarId = null)
			{
				XElement element = new(MR + "Result",
					new XElement(MR + "RequestId", requestId ?? ""),
					new XElement(MR + "Status", status));
				if (calendarId is not null)
					element.Add(new XElement(MR + "CalendarId", calendarId));
				return element;
			}

			try
			{
				if (options.Value.ReadOnly)
				{
					logger.LogInformation("Read-only: rejecting MeetingResponse for {RequestId} from {User}",
						requestId, context.Device.UserName);
					results.Add(Result("2"));
					continue;
				}

				if (requestId is null || collectionId is null)
				{
					results.Add(Result("2"));
					continue;
				}

				(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
					context.Session, context.Device.UserName, collectionId, ct);
				if (resolved is null)
				{
					results.Add(Result("2"));
					continue;
				}

				// Load the invite mail, extract the iCalendar payload.
				string? itemKey = await folders.ResolveItemKeyAsync(
					resolved.Value.Folder, resolved.Value.Store, requestId, ct);
				if (itemKey is null)
				{
					results.Add(Result("2"));
					continue;
				}

				byte[]? raw = await context.Session.MailStore.GetRawMessageAsync(
					resolved.Value.Folder.BackendKey, itemKey, ct);
				if (raw is null)
				{
					results.Add(Result("2"));
					continue;
				}

				using MemoryStream stream = new(raw);
				MimeMessage message = await MimeMessage.LoadAsync(stream, ct);
				MimePart? calendarPart = message.BodyParts.OfType<MimePart>()
					.FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
				if (calendarPart?.Content is null)
				{
					results.Add(Result("2"));
					continue;
				}

				using MemoryStream icsStream = new();
				await calendarPart.Content.DecodeToAsync(icsStream, ct);
				string ics = Encoding.UTF8.GetString(icsStream.ToArray());
				string? uid = ExtractUid(ics);

				// Update PARTSTAT in the user's calendar (if the event landed there already).
				string? calendarId = null;
				List<UserFolder> registry = await context.State.GetFoldersAsync(context.Device.UserName, ct);
				UserFolder? calendarFolder = registry.FirstOrDefault(f => f.Type == EasFolderType.Calendar);
				// Responding writes the attendee PARTSTAT into the calendar folder; a
				// read-only grant on it blocks that write the same way global ReadOnly does.
				if (calendarFolder is not null &&
				    WritePermission.IsBlocked(context, options.Value, calendarFolder))
				{
					logger.LogInformation(
						"Read-only folder: rejecting MeetingResponse for {RequestId} from {User}",
						requestId, context.Device.UserName);
					results.Add(Result("2"));
					continue;
				}

				if (uid is not null && calendarFolder is not null && context.Session.Calendar is not null)
				{
					string? href = await context.Session.Calendar.RespondToMeetingAsync(
						calendarFolder.BackendKey, uid, userResponse, ct);
					if (href is not null)
					{
						IContentStore calendarStore = context.Session.GetStoreForClass(EasClass.Calendar)!;
						calendarId = await folders.ComposeServerIdAsync(calendarFolder, calendarStore, href, ct);
					}
				}

				// Send the iTIP reply to the organizer.
				await SendReplyAsync(context, message, ics, userResponse, ct);

				results.Add(Result("1", calendarId));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A transient backend failure is a server error — status 4 (retryable), not
				// status 2 "invalid meeting request", which tells the client the request was
				// malformed. The explicit 2s above stay for genuinely bad input (F34).
				logger.LogError(ex, "MeetingResponse failed for {RequestId}", requestId);
				results.Add(Result("4"));
			}
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(MR + "MeetingResponse", results)));
	}

	/// <summary>
	///   Unfolds RFC 5545 (§3.1) folded content lines: a line continued onto the next begins with
	///   a single space or tab, which is stripped and the remainder appended to the previous line.
	///   Exchange/Google routinely fold UID and ORGANIZER past 75 octets, so every scan must run on
	///   the unfolded text or it truncates the value / loses the "mailto:" onto the continuation.
	/// </summary>
	internal static string Unfold(string ics)
	{
		StringBuilder builder = new(ics.Length);
		bool first = true;
		foreach (string raw in ics.Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			if (!first && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
			{
				builder.Append(line, 1, line.Length - 1);
			}
			else
			{
				if (!first)
					builder.Append('\n');
				builder.Append(line);
				first = false;
			}
		}

		return builder.ToString();
	}

	internal static string? ExtractUid(string ics)
	{
		return Unfold(ics).Split('\n')
			.FirstOrDefault(l => l.StartsWith("UID:", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
	}

	/// <summary>
	///   The organizer address to reply to: the iCalendar ORGANIZER's mailto value wins over the
	///   mail From header (the reply must go to the organizer, not always the sender). A minimal
	///   scan that tolerates CN=/other params before the value.
	/// </summary>
	internal static string? ExtractOrganizerEmail(string ics, string? fallbackAddress)
	{
		string? organizerLine = Unfold(ics).Split('\n')
			.FirstOrDefault(l => l.StartsWith("ORGANIZER", StringComparison.OrdinalIgnoreCase));
		return organizerLine is not null && organizerLine.Contains("mailto:", StringComparison.OrdinalIgnoreCase)
			? organizerLine[(organizerLine.IndexOf("mailto:", StringComparison.OrdinalIgnoreCase) + 7)..].Trim()
			: fallbackAddress;
	}

	private static async Task SendReplyAsync(
		EasContext context, MimeMessage invite, string ics, int userResponse, CancellationToken ct)
	{
		// Prefer the ORGANIZER from the iCalendar payload over the mail From header: the
		// reply (iTIP REPLY) must go to the meeting organizer, which is not always the
		// sender. We hand-scan the unfolded ICS line and take everything after "mailto:"
		// (offset 7) as the address — a deliberately minimal parse (no full iCal property
		// parsing) that tolerates the CN=/other params some servers add before the value.
		MailboxAddress? organizer = invite.From.Mailboxes.FirstOrDefault();
		string? organizerEmail = ExtractOrganizerEmail(ics, organizer?.Address);
		if (organizerEmail is null)
			return;

		string partStat = userResponse switch { 1 => "ACCEPTED", 2 => "TENTATIVE", 3 => "DECLINED", _ => "ACCEPTED" };
		string verb = userResponse switch { 1 => "Accepted", 2 => "Tentative", 3 => "Declined", _ => "Accepted" };
		// The iTIP REPLY needs the user's mail address; the gateway login is only a fallback
		// (identical in PassThrough, where MailAddress is the login when it contains '@').
		string user = context.Session.MailAddress ?? context.Device.UserName;
		string uid = ExtractUid(ics) ?? Guid.NewGuid().ToString();

		string replyIcs = new StringBuilder()
			.AppendLine("BEGIN:VCALENDAR")
			.AppendLine("PRODID:-//ActiveSync Gateway//EN")
			.AppendLine("VERSION:2.0")
			.AppendLine("METHOD:REPLY")
			.AppendLine("BEGIN:VEVENT")
			.AppendLine($"UID:{uid}")
			.AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}")
			.AppendLine($"ATTENDEE;PARTSTAT={partStat}:mailto:{user}")
			.AppendLine($"ORGANIZER:mailto:{organizerEmail}")
			.AppendLine("END:VEVENT")
			.AppendLine("END:VCALENDAR")
			.ToString();

		MimeMessage reply = ImipMailBuilder.Compose(
			user, [(organizerEmail, null)], $"{verb}: {invite.Subject}", "REPLY", replyIcs);
		using MemoryStream output = new();
		await reply.WriteToAsync(output, ct);
		await context.Session.MailSubmit.SendAsync(output.ToArray(), ct);
	}
}
