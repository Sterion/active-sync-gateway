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
///   picking one (F42).
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

	// F42 — three matching contacts for one "To" is ambiguous. Status 1 tells the client there is
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

	private static IReadOnlyList<XElement> Hit(string display, string email)
	{
		return [new XElement(GAL + "DisplayName", display), new XElement(GAL + "EmailAddress", email)];
	}

	private sealed class StubContacts(params IReadOnlyList<XElement>[] hits) : IContactOperations
	{
		public Task<IReadOnlyList<IReadOnlyList<XElement>>> SearchGalAsync(
			string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
		{
			IReadOnlyList<IReadOnlyList<XElement>> page = hits.Take(maxResults).ToList();
			return Task.FromResult(page);
		}
	}
}
