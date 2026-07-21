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
