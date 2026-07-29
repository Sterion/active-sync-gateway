using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Server.Eas;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Server.Tests;

public sealed class MeetingInvitationServiceTests
{
	// Added/removed recipients are diffed once against a single previous list. This covers the
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

	// A failed pre-change ICS read used to be swallowed with no signal, so the change hook
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

	/// <summary>
	///   A calendar store whose ordinary payload fetch fails. That fetch IS the raw read now (the
	///   dedicated GetRawEventAsync is gone with the typed currency), so it is what has to throw.
	/// </summary>
	private sealed class ThrowingCalendarStore : ICalendarStore, IMeetingOperations
	{
		public bool OwnsKey(FolderKey key) => true;

		public Task<CalendarItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct) =>
			throw new BackendException("transient DAV read failure");

		public Task<ItemKey?> RespondToMeetingAsync(
			FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct) => Task.FromResult(true);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
			FolderKey folder, ContentFilter filter, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
			FolderKey folder, CalendarItem item, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ItemRevision> UpdateItemAsync(
			FolderKey folder, ItemKey item, CalendarItem value, ItemRevision? expected, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
			IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
