using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Server.Tests;

/// <summary>
///   MeetingResponse (MS-ASCMD 2.2.1.11): a transient backend failure must read as retryable
///   status 4, not status 2 "invalid meeting request"; the invitation mail is removed after
///   a successful response; a calendar-collection CollectionId reads the event instead of
///   handing a calendar backend key to the mail store; a retry after the iTIP REPLY already
///   went out must not send a second one; an InstanceId (occurrence-scoped response) must not
///   silently respond for the whole series, since RespondToMeetingAsync has no occurrence-level
///   entry point; an absent/unparsable UserResponse must not default to the most-committing
///   answer, Accept.
/// </summary>
public sealed class MeetingResponseTests : IDisposable
{
	private static readonly XNamespace MR = EasNamespaces.MeetingResponse;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	private MeetingResponseHandler NewHandler()
	{
		return new MeetingResponseHandler(_harness.Folders,
			TestOptionsMonitor.SnapshotOf(_harness.Options), NullLogger<MeetingResponseHandler>.Instance);
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email });
		return registry.Single();
	}

	private Task<XDocument?> RunAsync(string requestId, string collectionId)
	{
		return _harness.RunAsync(NewHandler(), "MeetingResponse",
			new XDocument(new XElement(MR + "MeetingResponse",
				new XElement(MR + "Request",
					new XElement(MR + "RequestId", requestId),
					new XElement(MR + "CollectionId", collectionId),
					new XElement(MR + "UserResponse", "1")))));
	}

	private static string? StatusOf(XDocument? response)
	{
		return response?.Root?.Element(MR + "Result")?.Element(MR + "Status")?.Value;
	}

	/// <summary>Builds a MeetingResponse Request with full control over UserResponse/InstanceId.</summary>
	private Task<XDocument?> RunCustomAsync(
		string requestId, string collectionId, string? userResponse, string? instanceId = null)
	{
		XElement request = new(MR + "Request",
			new XElement(MR + "RequestId", requestId),
			new XElement(MR + "CollectionId", collectionId));
		if (userResponse is not null)
			request.Add(new XElement(MR + "UserResponse", userResponse));
		if (instanceId is not null)
			request.Add(new XElement(MR + "InstanceId", instanceId));

		return _harness.RunAsync(NewHandler(), "MeetingResponse",
			new XDocument(new XElement(MR + "MeetingResponse", request)));
	}

	// A backend failure while loading the invite must be status 4 (retryable server error),
	// not status 2 (invalid meeting request), which tells the client the request itself was bad.
	[Fact]
	public async Task BackendFailure_ReportsStatus4()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.GetRawFailWith = () => new BackendException("transient");

		XDocument? response = await RunAsync($"{inbox.ServerId}:42", inbox.ServerId);

		Assert.Equal("4", StatusOf(response));
	}

	// Control: genuinely malformed input (an unresolvable collection) stays status 2.
	[Fact]
	public async Task UnresolvableCollection_StaysStatus2()
	{
		XDocument? response = await RunAsync("imap:9999:42", "imap:9999");

		Assert.Equal("2", StatusOf(response));
	}

	// After a successful response the invitation mail must be removed from the Inbox (as
	// Exchange does), so the user is not left with a stale "respond to this invitation" message.
	[Fact]
	public async Task SuccessfulResponse_RemovesInvitationMail()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = InviteMime("evt-1", "organizer@example.test");

		XDocument? response = await RunAsync($"{inbox.ServerId}:42", inbox.ServerId);

		Assert.Equal("1", StatusOf(response));
		Assert.Single(_harness.Session.Submit.Sent); // the iTIP reply went out
		Assert.Equal(["imap:INBOX/42"], _harness.Session.Store.Deleted);
	}

	// Once the iTIP reply has been sent, a failure removing the invite mail must NOT report
	// retryable status 4: the client would retry the whole MeetingResponse and the organizer would
	// get a SECOND reply (and PARTSTAT would be written twice). The post-send tail is best-effort and
	// still returns status 1, mirroring ComposeMail.
	[Fact]
	public async Task ReplySent_ThenInviteDeleteFails_StaysStatus1()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = InviteMime("evt-1", "organizer@example.test");
		_harness.Session.Store.DeleteFailWith = () => new BackendException("IMAP hiccup removing invite");

		XDocument? response = await RunAsync($"{inbox.ServerId}:42", inbox.ServerId);

		Assert.Equal("1", StatusOf(response));
		Assert.Single(_harness.Session.Submit.Sent); // the iTIP reply went out exactly once
	}

	// A CollectionId that references a CALENDAR collection (responding to an already-filed
	// meeting) must read the event from the calendar store, not hand a calendar backend key to the
	// mail store (which fails). PARTSTAT is written to the calendar the request identified.
	[Fact]
	public async Task CalendarCollectionId_RespondsViaCalendarStore()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:evt-cal\r\n" +
			"ORGANIZER:mailto:organizer@example.test\r\nSUMMARY:Filed meeting\r\n" +
			"DTSTART:20260801T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
		EasHandlerHarness.RecordingStore calendar = new()
		{
			EasClass = EasClass.Calendar, KeyPrefix = "caldav:", RawEvent = ics, RespondHref = "event-href"
		};
		_harness.Session.SecondaryStore = calendar;
		_harness.Session.Calendar = calendar;

		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "caldav:Cal", DisplayName = "Calendar", Type = FolderType.Calendar, EasClass = EasClass.Calendar });
		UserFolder calFolder = registry.Single();
		// Map an item href → ServerId so the request's RequestId resolves back to it.
		string serverId = await _harness.Folders.ComposeServerIdAsync(
			calFolder, calendar, "event-href", CancellationToken.None);

		XDocument? response = await RunAsync(serverId, calFolder.ServerId);

		Assert.Equal("1", StatusOf(response));
		// PARTSTAT applied to the request's own calendar, and the iTIP reply went out.
		Assert.Equal(["caldav:Cal/evt-cal/1"], calendar.Responded);
		Assert.Single(_harness.Session.Submit.Sent);
	}

	// A resent MeetingResponse (the client never saw our response) must not re-send the iTIP
	// REPLY: the organizer must not receive a second reply for the same request.
	[Fact]
	public async Task ResentMeetingResponse_DoesNotSendASecondReply()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:evt-f7\r\n" +
			"ORGANIZER:mailto:organizer@example.test\r\nSUMMARY:Weekly sync\r\n" +
			"DTSTART:20260801T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
		EasHandlerHarness.RecordingStore calendar = new()
		{
			EasClass = EasClass.Calendar, KeyPrefix = "caldav:", RawEvent = ics, RespondHref = "event-href"
		};
		_harness.Session.SecondaryStore = calendar;
		_harness.Session.Calendar = calendar;

		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "caldav:Cal", DisplayName = "Calendar", Type = FolderType.Calendar, EasClass = EasClass.Calendar });
		UserFolder calFolder = registry.Single();
		string serverId = await _harness.Folders.ComposeServerIdAsync(
			calFolder, calendar, "event-href", CancellationToken.None);

		XDocument? first = await RunCustomAsync(serverId, calFolder.ServerId, "1");
		Assert.Equal("1", StatusOf(first));

		// The client never saw the first response and resends the IDENTICAL request.
		XDocument? second = await RunCustomAsync(serverId, calFolder.ServerId, "1");
		Assert.Equal("1", StatusOf(second));

		// The organizer must receive exactly ONE reply, not one per attempt.
		Assert.Single(_harness.Session.Submit.Sent);
	}

	// RespondToMeetingAsync has no occurrence-level entry point (only a whole-series UID), so
	// an InstanceId-scoped response must fail rather than silently respond (and mail the organizer)
	// for the WHOLE series.
	[Fact]
	public async Task InstanceIdScopedResponse_FailsInsteadOfRespondingForTheWholeSeries()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = InviteMime("evt-f8", "organizer@example.test");

		XDocument? response = await RunCustomAsync(
			$"{inbox.ServerId}:42", inbox.ServerId, "3", instanceId: "20260804T100000Z");

		Assert.Equal("2", StatusOf(response));
		// Neither the PARTSTAT write nor the iTIP reply may happen for an occurrence the gateway
		// cannot actually target.
		Assert.Empty(_harness.Session.Submit.Sent);
	}

	// An absent/unparsable UserResponse must not default to Accept, the most-committing (and
	// irreversible-mail-triggering) answer.
	[Theory]
	[InlineData(null)] // element omitted entirely
	[InlineData("bogus")] // present but not an integer
	[InlineData("9")] // present, parses, but out of the 1/2/3 range
	public async Task InvalidUserResponse_FailsInsteadOfDefaultingToAccept(string? userResponse)
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = InviteMime("evt-f9", "organizer@example.test");

		XDocument? response = await RunCustomAsync($"{inbox.ServerId}:42", inbox.ServerId, userResponse);

		Assert.Equal("2", StatusOf(response));
		Assert.Empty(_harness.Session.Submit.Sent);
	}

	private static byte[] InviteMime(string uid, string organizer)
	{
		MimeMessage message = new();
		message.From.Add(new MailboxAddress("Organizer", organizer));
		message.To.Add(new MailboxAddress("User", EasHandlerHarness.UserName));
		message.Subject = "Project sync";

		string ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
			$"UID:{uid}\r\nORGANIZER:mailto:{organizer}\r\nDTSTART:20260801T100000Z\r\n" +
			"SUMMARY:Project sync\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
		TextPart calendar = new("calendar") { Text = ics };
		message.Body = calendar;

		using MemoryStream stream = new();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
