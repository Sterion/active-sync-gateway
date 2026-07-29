using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   JMAP contacts store against a live JMAP-groupware server (Stalwart 0.16): exercises the
///   IContentStore CRUD surface and the JSContact converter end-to-end at the store layer
///   (below the EAS plumbing the mail/OOF stages already cover through the gateway). The store's
///   currency is vCard now — the EAS half is host-side — so these drive and assert vCard.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JmapContactStoreTests
{
	private static JmapContactStore Store()
	{
		JmapClient client = new(
			new Uri(TestBackend.JmapGroupwareUrl),
			new BackendCredentials { UserName = TestBackend.JmapGroupwareUser, Password = TestBackend.JmapGroupwarePassword },
			allowInvalidCertificates: true);
		return new JmapContactStore(client, 5);
	}

	/// <summary>A complete vCard — the shape the HOST hands the store after its own merge.</summary>
	private static ContactItem Card(string uid, params string[] properties)
	{
		string[] lines = ["BEGIN:VCARD", "VERSION:3.0", $"UID:{uid}", .. properties, "END:VCARD", ""];
		return new ContactItem { VCard = string.Join("\r\n", lines) };
	}

	/// <summary>The unfolded vCard property lines, so an assertion can name one exactly.</summary>
	private static IReadOnlyList<string> Lines(string vcf) =>
		vcf.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

	private static string? Value(string vcf, string property)
	{
		string? line = Lines(vcf).FirstOrDefault(l =>
			l.StartsWith(property + ":", StringComparison.Ordinal) ||
			l.StartsWith(property + ";", StringComparison.Ordinal));
		int colon = line?.IndexOf(':') ?? -1;
		return colon >= 0 ? line![(colon + 1)..] : null;
	}

	[JmapGroupwareFact]
	public async Task Contact_CreateGetUpdateDelete_RoundTrips()
	{
		JmapContactStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string surname = $"Lovelace{Guid.NewGuid():N}"[..12];
		string uid = Guid.NewGuid().ToString();
		ContactItem create = Card(uid,
			$"N:{surname};Ada;;;",
			$"FN:Ada {surname}",
			"EMAIL;TYPE=INTERNET:ada@example.com",
			"ORG:Analytical Engines;",
			"TEL;TYPE=CELL:+1-555-0100");

		(ItemKey itemKey, ItemRevision revision) = await store.CreateItemAsync(folderKey, create, CancellationToken.None);
		Assert.NotEmpty(itemKey.Value);
		Assert.NotEmpty(revision.Value);

		try
		{
			ContactItem? item = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(item);
			Assert.Equal($"{surname};Ada;;;", Value(item!.VCard, "N"));
			Assert.Equal("ada@example.com", Value(item.VCard, "EMAIL"));
			Assert.Equal("Analytical Engines;", Value(item.VCard, "ORG"));
			Assert.Equal("+1-555-0100", Value(item.VCard, "TEL"));

			IReadOnlyDictionary<ItemKey, ItemRevision> revs =
				await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
			Assert.Contains(itemKey, revs.Keys);

			// Full-item change (the host always hands over a COMPLETE payload): add a title, keep
			// the rest.
			ContactItem update = Card(uid,
				$"N:{surname};Ada;;;",
				$"FN:Ada {surname}",
				"EMAIL;TYPE=INTERNET:ada@example.com",
				"TITLE:Mathematician");
			await store.UpdateItemAsync(folderKey, itemKey, update, null, CancellationToken.None);

			ContactItem? updated = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.Equal("Mathematician", Value(updated!.VCard, "TITLE"));
			Assert.Equal("ada@example.com", Value(updated.VCard, "EMAIL"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}

		IReadOnlyDictionary<ItemKey, ItemRevision> after =
			await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
		Assert.DoesNotContain(itemKey, after.Keys);
	}

	/// <summary>
	///   Settles JMAP <c>*/set update</c> semantics against the live server: is the value a
	///   PatchObject (RFC 8620 §5.3, absent member = untouched) or a full replacement (absent
	///   member = cleared)? The host hands over the complete payload on every Change, so a field
	///   the client cleared arrives as an *absent* vCard property. If update patches, the gateway
	///   must send an explicit null for every managed member it did not write, or clearing never
	///   reaches the server.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Update_OmittingAManagedField_ClearsItOnTheServer()
	{
		JmapContactStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string surname = $"Clear{Guid.NewGuid():N}"[..12];
		string uid = Guid.NewGuid().ToString();
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Card(uid,
				$"N:{surname};Ada;;;",
				$"FN:Ada {surname}",
				"EMAIL;TYPE=INTERNET:ada@example.com",
				"TITLE:Mathematician",
				"TEL;TYPE=CELL:+1-555-0100"),
			CancellationToken.None);

		try
		{
			// The client cleared the title and the mobile number: both arrive as absent properties.
			await store.UpdateItemAsync(folderKey, itemKey,
				Card(uid,
					$"N:{surname};Ada;;;",
					$"FN:Ada {surname}",
					"EMAIL;TYPE=INTERNET:ada@example.com"),
				null, CancellationToken.None);

			ContactItem? updated = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(updated);
			Assert.Equal("ada@example.com", Value(updated!.VCard, "EMAIL"));
			Assert.Null(Value(updated.VCard, "TITLE"));
			Assert.Null(Value(updated.VCard, "TEL"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	/// <summary>
	///   The birthday was written into <c>anniversaries/b/date/utc</c> and read back out of
	///   <c>anniversaries/b/date/date</c>, so it never survived a round trip. Live, because the
	///   unit test cannot show that the server also stores and returns the member.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Contact_Birthday_RoundTripsThroughTheServer()
	{
		JmapContactStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string surname = $"Bday{Guid.NewGuid():N}"[..11];
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Card(Guid.NewGuid().ToString(), $"N:{surname};Ada;;;", $"FN:Ada {surname}", "BDAY:1815-12-10"),
			CancellationToken.None);

		try
		{
			ContactItem? item = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			string? birthday = Value(item!.VCard, "BDAY");
			Assert.NotNull(birthday);
			Assert.StartsWith("1815-12-10", birthday);
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	[JmapGroupwareFact]
	public async Task GalSearch_FindsCreatedContact()
	{
		JmapContactStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;
		string token = $"Gal{Guid.NewGuid():N}"[..10];

		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Card(Guid.NewGuid().ToString(),
				$"N:Tester;{token};;;",
				$"FN:{token} Tester",
				$"EMAIL;TYPE=INTERNET:{token}@example.com"),
			CancellationToken.None);

		try
		{
			IReadOnlyList<GalEntry> hits = await store.SearchGalAsync(token, 20, null, CancellationToken.None);
			Assert.Contains(hits, entry => entry.EmailAddress == $"{token}@example.com");
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}
}
