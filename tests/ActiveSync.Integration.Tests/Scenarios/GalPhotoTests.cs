using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   GAL contact photos end-to-end (local contact store): a contact synced with a Picture
///   comes back as photo Data in Search-GAL and ResolveRecipients when the client asks,
///   honoring MaxPictures.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class GalPhotoTests(GatewayFixture gateway)
{
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace S = EasNamespaces.Search;
	private static readonly XNamespace RR = EasNamespaces.ResolveRecipients;
	private static readonly XNamespace Gal = EasNamespaces.Gal;
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 9, 8, 7, 6, 5];

	[BackendFact]
	public async Task GalSearchAndResolveRecipients_ReturnContactPhotos_OnRequest()
	{
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		await client.HandshakeAsync();
		string marker = $"GP{Guid.NewGuid():N}"[..10];
		string address = $"{marker.ToLowerInvariant()}@example.com";

		string contacts = client.FolderOfType(EasFolderType.Contacts).ServerId;
		await client.InitialSyncAsync(contacts);
		await client.PullAllAsync(contacts);
		SyncResult add = await client.AddItemAsync(contacts, "gp1",
			new XElement(C + "FirstName", "Photo"),
			new XElement(C + "LastName", marker),
			new XElement(C + "Email1Address", address),
			new XElement(C + "Picture", Convert.ToBase64String(PhotoBytes)));
		Assert.Equal("1", add.Status);

		// --- Search GAL with a photo request ---
		XDocument? search = await client.PostAsync("Search", new XDocument(
			new XElement(S + "Search",
				new XElement(S + "Store",
					new XElement(S + "Name", "GAL"),
					new XElement(S + "Query", marker),
					new XElement(S + "Options",
						new XElement(S + "Range", "0-9"),
						new XElement(S + "Picture",
							new XElement(S + "MaxSize", "65536"),
							new XElement(S + "MaxPictures", "5")))))));
		XElement? result = search?.Descendants(S + "Result")
			.FirstOrDefault(r => r.Descendants(Gal + "LastName").Any(e => e.Value == marker));
		Assert.NotNull(result);
		XElement? galPicture = result.Descendants(Gal + "Picture").FirstOrDefault();
		Assert.NotNull(galPicture);
		Assert.Equal("1", galPicture.Element(Gal + "Status")?.Value);
		Assert.Equal(PhotoBytes, Convert.FromBase64String(galPicture.Element(Gal + "Data")!.Value));

		// --- MaxPictures 0: the photo is withheld with status 175 ---
		XDocument? capped = await client.PostAsync("Search", new XDocument(
			new XElement(S + "Search",
				new XElement(S + "Store",
					new XElement(S + "Name", "GAL"),
					new XElement(S + "Query", marker),
					new XElement(S + "Options",
						new XElement(S + "Range", "0-9"),
						new XElement(S + "Picture",
							new XElement(S + "MaxPictures", "0")))))));
		XElement? cappedPicture = capped?.Descendants(Gal + "Picture").FirstOrDefault();
		Assert.Equal("175", cappedPicture?.Element(Gal + "Status")?.Value);

		// --- ResolveRecipients with a photo request ---
		XDocument? resolved = await client.PostAsync("ResolveRecipients", new XDocument(
			new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", marker),
				new XElement(RR + "Options",
					new XElement(RR + "Picture",
						new XElement(RR + "MaxSize", "65536"),
						new XElement(RR + "MaxPictures", "3"))))));
		XElement? recipient = resolved?.Descendants(RR + "Recipient")
			.FirstOrDefault(r => r.Element(RR + "EmailAddress")?.Value == address);
		Assert.NotNull(recipient);
		XElement? rrPicture = recipient.Element(RR + "Picture");
		Assert.NotNull(rrPicture);
		Assert.Equal("1", rrPicture.Element(RR + "Status")?.Value);
		Assert.Equal(PhotoBytes, Convert.FromBase64String(rrPicture.Element(RR + "Data")!.Value));
	}
}
