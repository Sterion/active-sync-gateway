using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   ResolveRecipients (MS-ASCMD 2.2.1.15) status conformance: an ambiguous match must report
///   status 2/3 (ambiguous), not 1 (single match), so the client prompts instead of silently
///   picking one.
/// </summary>
public sealed class ResolveRecipientsTests : IDisposable
{
	private static readonly XNamespace RR = EasNamespaces.ResolveRecipients;
	private static readonly XNamespace GAL = EasNamespaces.Gal;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// Three matching contacts for one "To" is ambiguous. Status 1 tells the client there is
	// exactly one and it picks arbitrarily; it must be an ambiguity status (3 = all returned).
	[Fact]
	public async Task AmbiguousMatch_ReportsAmbiguousStatus_NotSingleMatch()
	{
		_harness.Session.Contacts = new StubContacts(
			Hit("Jon Alpha", "jon.alpha@example.test"),
			Hit("Jon Bravo", "jon.bravo@example.test"),
			Hit("Jon Charlie", "jon.charlie@example.test"));

		XDocument? response = await _harness.RunAsync(
			new ResolveRecipientsHandler(NullLogger<ResolveRecipientsHandler>.Instance),
			"ResolveRecipients",
			new XDocument(new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", "Jon"))));

		XElement? resp = response?.Root?.Element(RR + "Response");
		Assert.Equal("3", resp?.Element(RR + "Status")?.Value);
		Assert.Equal("3", resp?.Element(RR + "RecipientCount")?.Value);
	}

	// A single match stays status 1.
	[Fact]
	public async Task SingleMatch_ReportsStatus1()
	{
		_harness.Session.Contacts = new StubContacts(Hit("Jon Alpha", "jon.alpha@example.test"));

		XDocument? response = await _harness.RunAsync(
			new ResolveRecipientsHandler(NullLogger<ResolveRecipientsHandler>.Instance),
			"ResolveRecipients",
			new XDocument(new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", "Jon"))));

		XElement? resp = response?.Root?.Element(RR + "Response");
		Assert.Equal("1", resp?.Element(RR + "Status")?.Value);
	}

	// Coverage: the per-To lookups now run concurrently (Task.WhenAll). The observable
	// contract that must survive that change is ORDER: each To's Response stays in request order and
	// carries its own match. (The concurrency itself is not unit-observable with synchronous stubs.)
	[Fact]
	public async Task MultipleTos_ResponsesStayInRequestOrderWithTheirOwnMatch()
	{
		_harness.Session.Contacts = new QueryContacts(new Dictionary<string, GalEntry>
		{
			["alice"] = Hit("Alice", "alice@example.test"),
			["bob"] = Hit("Bob", "bob@example.test"),
			["carol"] = Hit("Carol", "carol@example.test")
		});

		XDocument? response = await _harness.RunAsync(
			new ResolveRecipientsHandler(NullLogger<ResolveRecipientsHandler>.Instance),
			"ResolveRecipients",
			new XDocument(new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", "alice"),
				new XElement(RR + "To", "bob"),
				new XElement(RR + "To", "carol"))));

		List<XElement> resps = response!.Root!.Elements(RR + "Response").ToList();
		Assert.Equal(["alice", "bob", "carol"], resps.Select(r => r.Element(RR + "To")!.Value));
		Assert.Equal("alice@example.test",
			resps[0].Element(RR + "Recipient")!.Element(RR + "EmailAddress")!.Value);
		Assert.Equal("carol@example.test",
			resps[2].Element(RR + "Recipient")!.Element(RR + "EmailAddress")!.Value);
	}

	// MS-ASCMD's Recipient sequence is Type, DisplayName, EmailAddress,
	// Availability, Certificates, Picture. A strict-sequence client drops the free/busy digit
	// string it explicitly asked for if Picture arrives before Availability.
	[Fact]
	public async Task RecipientWithPictureAndAvailability_EmitsAvailabilityBeforePicture()
	{
		_harness.Session.Contacts = new StubContacts(HitWithPicture("Jon Alpha", "jon.alpha@example.test"));

		XDocument? response = await _harness.RunAsync(
			new ResolveRecipientsHandler(NullLogger<ResolveRecipientsHandler>.Instance),
			"ResolveRecipients",
			new XDocument(new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", "Jon"),
				new XElement(RR + "Options",
					new XElement(RR + "Picture",
						new XElement(RR + "MaxSize", "0")),
					new XElement(RR + "Availability",
						new XElement(RR + "StartTime", "2026-07-28T00:00:00.000Z"),
						new XElement(RR + "EndTime", "2026-07-29T00:00:00.000Z"))))));

		XElement recipient = response!.Root!.Element(RR + "Response")!.Element(RR + "Recipient")!;
		List<string> childNames = recipient.Elements().Select(e => e.Name.LocalName).ToList();

		int availabilityIndex = childNames.IndexOf("Availability");
		int pictureIndex = childNames.IndexOf("Picture");
		Assert.True(availabilityIndex >= 0, "Availability must be present");
		Assert.True(pictureIndex >= 0, "Picture must be present");
		Assert.True(availabilityIndex < pictureIndex,
			$"Availability (index {availabilityIndex}) must precede Picture (index {pictureIndex})");
	}

	// The store hands over TYPED GAL entries now; the RR-namespace shaping (including the photo
	// status) is the host's, which is exactly what these tests exercise.
	private static GalEntry Hit(string display, string email)
	{
		return new GalEntry { DisplayName = display, EmailAddress = email };
	}

	private static GalEntry HitWithPicture(string display, string email)
	{
		return new GalEntry
		{
			DisplayName = display,
			EmailAddress = email,
			Picture = new GalPictureResult
			{
				Status = GalPictureStatus.Available,
				Picture = new GalPicture { Data = "fake-photo-bytes"u8.ToArray(), ContentType = "image/jpeg" }
			}
		};
	}

	/// <summary>A GAL that answers each query with its own configured match set.</summary>
	private sealed class QueryContacts(Dictionary<string, GalEntry> byQuery) : IDirectoryOperations
	{
		public Task<IReadOnlyList<GalEntry>> SearchGalAsync(
			string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
		{
			IReadOnlyList<GalEntry> page = byQuery.TryGetValue(query, out GalEntry? hit) ? [hit] : [];
			return Task.FromResult(page);
		}
	}

	private sealed class StubContacts(params GalEntry[] hits) : IDirectoryOperations
	{
		public Task<IReadOnlyList<GalEntry>> SearchGalAsync(
			string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
		{
			IReadOnlyList<GalEntry> page = hits.Take(maxResults).ToList();
			return Task.FromResult(page);
		}
	}
}
