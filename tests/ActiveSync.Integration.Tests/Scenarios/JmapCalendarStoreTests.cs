using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   JMAP calendar store against a live JMAP-groupware server (Stalwart 0.16): the
///   JSCalendar ⇄ iCalendar ⇄ EAS bridge end-to-end at the store layer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JmapCalendarStoreTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static JmapCalendarStore Store()
	{
		JmapClient client = new(
			new Uri(TestBackend.JmapGroupwareUrl),
			new BackendCredentials(TestBackend.JmapGroupwareUser, TestBackend.JmapGroupwarePassword),
			allowInvalidCertificates: true);
		return new JmapCalendarStore(client, "admin@example.com", 5);
	}

	[JmapGroupwareFact]
	public async Task Event_CreateGetUpdateDelete_RoundTrips()
	{
		JmapCalendarStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string subject = $"Sprint Review {Guid.NewGuid():N}"[..20];
		XElement create = new("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", "20260720T100000Z"),
			new XElement(Cal + "EndTime", "20260720T110000Z"),
			new XElement(Cal + "Location", "Room 1"),
			new XElement(Cal + "BusyStatus", "2"));

		(string itemKey, string revision) = await store.CreateItemAsync(folderKey, create, CancellationToken.None);
		Assert.NotEmpty(itemKey);
		Assert.NotEmpty(revision);

		try
		{
			BackendItem? item = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.NotNull(item);
			string? V(BackendItem i, string local) => i.ApplicationData.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal(subject, V(item, "Subject"));
			Assert.Equal("Room 1", V(item, "Location"));
			Assert.Equal("20260720T100000Z", V(item, "StartTime"));
			Assert.Equal("20260720T110000Z", V(item, "EndTime"));

			IReadOnlyDictionary<string, string> revs =
				await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
			Assert.Contains(itemKey, revs.Keys);

			// GetRawEvent returns iCalendar (the invitation service reads it).
			string? ics = await store.GetRawEventAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(ics);
			Assert.Contains("BEGIN:VEVENT", ics);

			XElement update = new("ApplicationData",
				new XElement(Cal + "Subject", subject + " (moved)"),
				new XElement(Cal + "StartTime", "20260720T100000Z"),
				new XElement(Cal + "EndTime", "20260720T110000Z"),
				new XElement(Cal + "Location", "Room 2"),
				new XElement(Cal + "BusyStatus", "2"));
			await store.UpdateItemAsync(folderKey, itemKey, update, CancellationToken.None);

			BackendItem? updated = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.Equal("Room 2", V(updated!, "Location"));
			Assert.Equal(subject + " (moved)", V(updated!, "Subject"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, CancellationToken.None);
		}

		IReadOnlyDictionary<string, string> after =
			await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
		Assert.DoesNotContain(itemKey, after.Keys);
	}
}
