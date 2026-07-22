using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Server.Eas;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Server.Tests;

public sealed class MeetingInvitationServiceTests
{
	// E24: added/removed recipients are diffed once against a single previous list. This covers the
	// behaviour the O(n²)→O(n) hoist must preserve (it is a mechanical refactor, so this documents
	// the contract rather than reproducing a bug).
	[Fact]
	public void DiffRecipients_PreviousKnown_ComputesAddedAndRemoved()
	{
		List<(string Email, string? Name)> previous = [("a@x", "A"), ("b@x", "B")];
		List<(string Email, string? Name)> current = [("B@x", "B"), ("c@x", "C")]; // b kept (case-insensitive), a gone, c new

		(var added, var removed) = MeetingInvitationService.DiffRecipients(previous, current, previousKnown: true);

		Assert.Equal(["c@x"], added.Select(a => a.Email));
		Assert.Equal(["a@x"], removed.Select(r => r.Email));
	}

	[Fact]
	public void DiffRecipients_PreviousUnknown_TreatsEveryoneAsAdded()
	{
		List<(string Email, string? Name)> current = [("a@x", "A"), ("b@x", "B")];

		(var added, var removed) = MeetingInvitationService.DiffRecipients([], current, previousKnown: false);

		Assert.Equal(["a@x", "b@x"], added.Select(a => a.Email));
		Assert.Empty(removed);
	}

	// E34: a failed pre-change ICS read used to be swallowed with no signal, so the change hook
	// silently re-invited every attendee. The failure must now be logged.
	[Fact]
	public async Task CaptureIcsAsync_ReadFailure_IsLogged_NotSilentlySwallowed()
	{
		CapturingLogger logger = new();
		ThrowingCalendarStore store = new();

		string? ics = await MeetingInvitationService.CaptureIcsAsync(
			store, "caldav:cal", "item-1", logger, CancellationToken.None);

		Assert.Null(ics); // still degrades to "no previous state"
		Assert.Contains(logger.Warnings, w => w.Contains("item-1"));
	}

	private sealed class CapturingLogger : ILogger
	{
		public List<string> Warnings { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Warning)
				Warnings.Add(formatter(state, exception));
		}
	}

	private sealed class ThrowingCalendarStore : IContentStore, ICalendarOperations
	{
		public string EasClass => Protocol.EasClass.Calendar;
		public bool OwnsBackendKey(string backendKey) => true;

		public Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct) =>
			throw new BackendException("transient DAV read failure");

		public Task<string?> RespondToMeetingAsync(
			string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct) => Task.FromResult(true);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
