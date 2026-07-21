using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   JMAP contacts store against a live JMAP-groupware server (Stalwart 0.16): exercises the
///   IContentStore CRUD surface and the JSContact converter end-to-end at the store layer
///   (below the EAS plumbing the mail/OOF stages already cover through the gateway).
/// </summary>
[Trait("Category", "Integration")]
public sealed class JmapContactStoreTests
{
	private static readonly XNamespace C = EasNamespaces.Contacts;

	private static JmapContactStore Store()
	{
		JmapClient client = new(
			new Uri(TestBackend.JmapGroupwareUrl),
			new BackendCredentials(TestBackend.JmapGroupwareUser, TestBackend.JmapGroupwarePassword),
			allowInvalidCertificates: true);
		return new JmapContactStore(client, 5);
	}

	[JmapGroupwareFact]
	public async Task Contact_CreateGetUpdateDelete_RoundTrips()
	{
		JmapContactStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string surname = $"Lovelace{Guid.NewGuid():N}"[..12];
		XElement create = new("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", surname),
			new XElement(C + "Email1Address", "ada@example.com"),
			new XElement(C + "CompanyName", "Analytical Engines"),
			new XElement(C + "MobilePhoneNumber", "+1-555-0100"));

		(string itemKey, string revision) = await store.CreateItemAsync(folderKey, create, CancellationToken.None);
		Assert.NotEmpty(itemKey);
		Assert.NotEmpty(revision);

		try
		{
			BackendItem? item = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.NotNull(item);
			string? V(BackendItem i, string local) => i.ApplicationData.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal("Ada", V(item, "FirstName"));
			Assert.Equal(surname, V(item, "LastName"));
			Assert.Equal("ada@example.com", V(item, "Email1Address"));
			Assert.Equal("Analytical Engines", V(item, "CompanyName"));
			Assert.Equal("+1-555-0100", V(item, "MobilePhoneNumber"));

			IReadOnlyDictionary<string, string> revs =
				await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
			Assert.Contains(itemKey, revs.Keys);

			// Full-item change (EAS sends the complete managed set): change title, keep the rest.
			XElement update = new("ApplicationData",
				new XElement(C + "FirstName", "Ada"),
				new XElement(C + "LastName", surname),
				new XElement(C + "Email1Address", "ada@example.com"),
				new XElement(C + "JobTitle", "Mathematician"));
			await store.UpdateItemAsync(folderKey, itemKey, update, CancellationToken.None);

			BackendItem? updated = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.Equal("Mathematician", V(updated!, "JobTitle"));
			Assert.Equal("ada@example.com", V(updated!, "Email1Address"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, CancellationToken.None);
		}

		IReadOnlyDictionary<string, string> after =
			await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
		Assert.DoesNotContain(itemKey, after.Keys);
	}

	/// <summary>
	///   H7 — settles JMAP <c>*/set update</c> semantics against the live server: is the value a
	///   PatchObject (RFC 8620 §5.3, absent member = untouched) or a full replacement (absent
	///   member = cleared)? EAS sends the complete managed set on every Change, so a field the
	///   client cleared arrives as an *absent* element. If update patches, the gateway must send
	///   an explicit null for every managed member it did not write, or clearing never reaches
	///   the server.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Update_OmittingAManagedField_ClearsItOnTheServer()
	{
		JmapContactStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string surname = $"Clear{Guid.NewGuid():N}"[..12];
		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", surname),
			new XElement(C + "Email1Address", "ada@example.com"),
			new XElement(C + "JobTitle", "Mathematician"),
			new XElement(C + "MobilePhoneNumber", "+1-555-0100")), CancellationToken.None);

		try
		{
			// The client cleared JobTitle and the mobile number: both arrive as absent elements.
			await store.UpdateItemAsync(folderKey, itemKey, new XElement("ApplicationData",
				new XElement(C + "FirstName", "Ada"),
				new XElement(C + "LastName", surname),
				new XElement(C + "Email1Address", "ada@example.com")), CancellationToken.None);

			BackendItem? updated =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.NotNull(updated);
			string? V(string local) => updated.ApplicationData.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal("ada@example.com", V("Email1Address"));
			Assert.Null(V("JobTitle"));
			Assert.Null(V("MobilePhoneNumber"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, CancellationToken.None);
		}
	}

	/// <summary>
	///   H6 — the birthday was written into <c>anniversaries/b/date/utc</c> and read back out of
	///   <c>anniversaries/b/date/date</c>, so it never survived a round trip. Live, because the
	///   unit test cannot show that the server also stores and returns the member.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Contact_Birthday_RoundTripsThroughTheServer()
	{
		JmapContactStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string surname = $"Bday{Guid.NewGuid():N}"[..11];
		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", surname),
			new XElement(C + "Birthday", "1815-12-10T00:00:00.000Z")), CancellationToken.None);

		try
		{
			BackendItem? item =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			string? birthday = item!.ApplicationData.FirstOrDefault(e => e.Name.LocalName == "Birthday")?.Value;
			Assert.NotNull(birthday);
			Assert.StartsWith("1815-12-10", birthday);
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, CancellationToken.None);
		}
	}

	[JmapGroupwareFact]
	public async Task GalSearch_FindsCreatedContact()
	{
		JmapContactStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;
		string token = $"Gal{Guid.NewGuid():N}"[..10];

		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(C + "FirstName", token),
			new XElement(C + "LastName", "Tester"),
			new XElement(C + "Email1Address", $"{token}@example.com")), CancellationToken.None);

		try
		{
			IReadOnlyList<IReadOnlyList<XElement>> hits =
				await store.SearchGalAsync(token, 20, null, CancellationToken.None);
			Assert.Contains(hits, entry => entry.Any(e =>
				e.Name.LocalName == "EmailAddress" && e.Value == $"{token}@example.com"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, CancellationToken.None);
		}
	}
}
