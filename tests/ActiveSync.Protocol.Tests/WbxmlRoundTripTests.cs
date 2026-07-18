using System.Text;
using System.Xml.Linq;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

public class WbxmlRoundTripTests
{
	private static readonly XNamespace AirSync = EasNamespaces.AirSync;
	private static readonly XNamespace FolderHierarchy = EasNamespaces.FolderHierarchy;
	private static readonly XNamespace ComposeMail = EasNamespaces.ComposeMail;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Search = EasNamespaces.Search;

	private static XDocument RoundTrip(XDocument doc)
	{
		byte[] bytes = WbxmlEncoder.Encode(doc);
		return WbxmlDecoder.Decode(bytes);
	}

	[Fact]
	public void FolderSyncRequest_RoundTrips()
	{
		XDocument doc = new(
			new XElement(FolderHierarchy + "FolderSync",
				new XElement(FolderHierarchy + "SyncKey", "0")));

		XDocument result = RoundTrip(doc);

		Assert.Equal("FolderSync", result.Root!.Name.LocalName);
		Assert.Equal(FolderHierarchy, result.Root.Name.Namespace);
		Assert.Equal("0", result.Root.Element(FolderHierarchy + "SyncKey")!.Value);
	}

	[Fact]
	public void SyncRequest_WithMultipleCodePages_RoundTrips()
	{
		XDocument doc = new(
			new XElement(AirSync + "Sync",
				new XElement(AirSync + "Collections",
					new XElement(AirSync + "Collection",
						new XElement(AirSync + "SyncKey", "1234"),
						new XElement(AirSync + "CollectionId", "5"),
						new XElement(AirSync + "DeletesAsMoves", "1"),
						new XElement(AirSync + "GetChanges"),
						new XElement(AirSync + "WindowSize", "50"),
						new XElement(AirSync + "Options",
							new XElement(AirSync + "FilterType", "2"),
							new XElement(AirSyncBase + "BodyPreference",
								new XElement(AirSyncBase + "Type", "2"),
								new XElement(AirSyncBase + "TruncationSize", "51200")))))));

		XDocument result = RoundTrip(doc);

		XElement collection = result.Root!
			.Element(AirSync + "Collections")!
			.Element(AirSync + "Collection")!;
		Assert.Equal("1234", collection.Element(AirSync + "SyncKey")!.Value);
		Assert.Equal("5", collection.Element(AirSync + "CollectionId")!.Value);
		// Empty element must survive
		Assert.NotNull(collection.Element(AirSync + "GetChanges"));
		Assert.Empty(collection.Element(AirSync + "GetChanges")!.Value);
		// Nested code-page switch (AirSync -> AirSyncBase -> back)
		XElement bodyPref = collection.Element(AirSync + "Options")!.Element(AirSyncBase + "BodyPreference")!;
		Assert.Equal("2", bodyPref.Element(AirSyncBase + "Type")!.Value);
		Assert.Equal("51200", bodyPref.Element(AirSyncBase + "TruncationSize")!.Value);
	}

	[Fact]
	public void OpaqueData_RoundTrips()
	{
		byte[] mime = Encoding.UTF8.GetBytes("From: a@b.c\r\nTo: d@e.f\r\nSubject: Hi\r\n\r\nBody text");
		XElement mimeElement = new(ComposeMail + "Mime", Convert.ToBase64String(mime));
		mimeElement.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(
			new XElement(ComposeMail + "SendMail",
				new XElement(ComposeMail + "ClientId", "abc123"),
				new XElement(ComposeMail + "SaveInSentItems"),
				mimeElement));

		XDocument result = RoundTrip(doc);

		XElement decoded = result.Root!.Element(ComposeMail + "Mime")!;
		Assert.Equal("1", (string?)decoded.Attribute(EasNamespaces.OpaqueAttribute));
		Assert.Equal(mime, Convert.FromBase64String(decoded.Value));
	}

	[Fact]
	public void NonAsciiText_RoundTrips()
	{
		XDocument doc = new(
			new XElement(FolderHierarchy + "FolderCreate",
				new XElement(FolderHierarchy + "DisplayName", "Følder Ærø — 日本語 ✓")));

		XDocument result = RoundTrip(doc);

		Assert.Equal("Følder Ærø — 日本語 ✓", result.Root!.Element(FolderHierarchy + "DisplayName")!.Value);
	}

	[Fact]
	public void KnownBinary_FolderSyncRequest_Decodes()
	{
		// 03 01 6a 00 = header; 00 07 = switch to FolderHierarchy page;
		// 56 = FolderSync with content; 52 = SyncKey with content; 03 '0' 00 = STR_I "0"; 01 01 = END END
		byte[] wbxml = [0x03, 0x01, 0x6A, 0x00, 0x00, 0x07, 0x56, 0x52, 0x03, (byte)'0', 0x00, 0x01, 0x01];

		XDocument doc = WbxmlDecoder.Decode(wbxml);

		Assert.Equal(FolderHierarchy + "FolderSync", doc.Root!.Name);
		Assert.Equal("0", doc.Root.Element(FolderHierarchy + "SyncKey")!.Value);
	}

	[Fact]
	public void Encode_ProducesCanonicalHeader()
	{
		XDocument doc = new(new XElement(AirSync + "Sync"));
		byte[] bytes = WbxmlEncoder.Encode(doc);

		// version 1.3, unknown public id, UTF-8, empty string table, then Sync tag (0x05, no content)
		Assert.Equal(new byte[] { 0x03, 0x01, 0x6A, 0x00, 0x05 }, bytes);
	}

	[Fact]
	public void UnknownToken_Throws()
	{
		// valid header, then token 0x3F (unknown on AirSync page)
		byte[] wbxml = [0x03, 0x01, 0x6A, 0x00, 0x3F];
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(wbxml));
	}

	[Fact]
	public void SearchSchemaAndSupported_RoundTrip()
	{
		// Tokens 0x1C/0x1D — previously missing from the Search page, so a client that
		// tokenized these would have been rejected with a 400.
		XDocument doc = new(
			new XElement(Search + "Search",
				new XElement(Search + "Store",
					new XElement(Search + "Name", "Mailbox"),
					new XElement(Search + "Options",
						new XElement(Search + "Schema",
							new XElement(Search + "Supported"))))));

		XDocument result = RoundTrip(doc);
		XElement schema = result.Root!.Element(Search + "Store")!.Element(Search + "Options")!
			.Element(Search + "Schema")!;
		Assert.NotNull(schema.Element(Search + "Supported"));
	}

	[Fact]
	public void ComposeMailType_RoundTrips()
	{
		// Token 0x0A — previously missing from the ComposeMail page.
		XDocument doc = new(
			new XElement(ComposeMail + "SendMail",
				new XElement(ComposeMail + "ClientId", "c1"),
				new XElement(ComposeMail + "Type", "SMS")));

		XDocument result = RoundTrip(doc);
		Assert.Equal("SMS", result.Root!.Element(ComposeMail + "Type")!.Value);
	}

	[Fact]
	public void MultiByteInteger_LargeOpaque_RoundTrips()
	{
		byte[] payload = new byte[300]; // length > 127 forces multi-byte length encoding
		Random.Shared.NextBytes(payload);
		XElement element = new(ComposeMail + "Mime", Convert.ToBase64String(payload));
		element.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(new XElement(ComposeMail + "SendMail", element));

		XDocument result = RoundTrip(doc);

		Assert.Equal(payload, Convert.FromBase64String(result.Root!.Element(ComposeMail + "Mime")!.Value));
	}
}
