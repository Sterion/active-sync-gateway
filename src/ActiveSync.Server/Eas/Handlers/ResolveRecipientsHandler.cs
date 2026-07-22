using System.Globalization;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   ResolveRecipients (MS-ASCMD 2.2.1.15): CardDAV-backed lookup with optional contact
///   photos and free/busy (Availability → MergedFreeBusy); no certificate retrieval.
/// </summary>
public sealed class ResolveRecipientsHandler(ILogger<ResolveRecipientsHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace RR = EasNamespaces.ResolveRecipients;
	private static readonly XNamespace GAL = EasNamespaces.Gal;

	public string Command => "ResolveRecipients";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		List<string> tos = request?.Root?.Elements(RR + "To").Select(t => t.Value).ToList() ?? [];
		XElement? options = request?.Root?.Element(RR + "Options");

		// The client's cap on ambiguous matches (MS-ASCMD): honour it as both the GAL fetch limit
		// and the "more than returned" threshold, rather than the hard-coded 10.
		int maxAmbiguous =
			int.TryParse(options?.Element(RR + "MaxAmbiguousRecipients")?.Value, out int max) && max > 0
				? max
				: 10;

		// Optional photos: Options > Picture (MaxSize, MaxPictures) — RR namespace.
		GalPhotoRequest? photos = null;
		if (options?.Element(RR + "Picture") is XElement picture)
			photos = new GalPhotoRequest(
				int.TryParse(picture.Element(RR + "MaxSize")?.Value, out int maxSize) ? maxSize : null,
				int.TryParse(picture.Element(RR + "MaxPictures")?.Value, out int maxCount) ? maxCount : null);

		// Optional free/busy: Options > Availability (StartTime, EndTime).
		(DateTime StartUtc, DateTime EndUtc)? availabilityWindow = null;
		if (options?.Element(RR + "Availability") is XElement availability &&
		    TryParseTime(availability.Element(RR + "StartTime")?.Value, out DateTime windowStart) &&
		    TryParseTime(availability.Element(RR + "EndTime")?.Value, out DateTime windowEnd) &&
		    windowEnd > windowStart)
			availabilityWindow = (windowStart, windowEnd);

		// F43: each To is independent; run them concurrently rather than one SearchGalAsync (and its
		// nested free/busy fetches) after another while a user watches a compose screen. Task.WhenAll
		// preserves input order, so the responses still line up with the To list.
		XElement[] responses = await Task.WhenAll(
			tos.Select(to => ProcessToAsync(context, to, maxAmbiguous, photos, availabilityWindow, ct)));

		await context.WriteResponseAsync(new XDocument(
			new XElement(RR + "ResolveRecipients",
				new XElement(RR + "Status", "1"),
				responses)));
	}

	private async Task<XElement> ProcessToAsync(
		EasContext context, string to, int maxAmbiguous, GalPhotoRequest? photos,
		(DateTime StartUtc, DateTime EndUtc)? availabilityWindow, CancellationToken ct)
	{
		List<XElement> recipients = new();
		int galHits = 0;
		if (context.Session.Contacts is not null)
		{
			IReadOnlyList<IReadOnlyList<XElement>> hits =
				await context.Session.Contacts.SearchGalAsync(to, maxAmbiguous, photos, ct);
			galHits = hits.Count;

			// Build the recipient skeletons first, collecting the email of each so the free/busy
			// lookups for the whole match set can run concurrently rather than one after another.
			List<(XElement Recipient, string Email)> built = new();
			foreach (IReadOnlyList<XElement> hit in hits)
			{
				string display = hit.FirstOrDefault(e => e.Name == GAL + "DisplayName")?.Value ?? to;
				string? email = hit.FirstOrDefault(e => e.Name == GAL + "EmailAddress")?.Value;
				if (email is null)
					continue;
				XElement recipient = new(RR + "Recipient",
					new XElement(RR + "Type", "2"),
					new XElement(RR + "DisplayName", display),
					new XElement(RR + "EmailAddress", email));
				// The GAL photo element translates into the RR-namespace shape.
				if (hit.FirstOrDefault(e => e.Name == GAL + "Picture") is XElement galPicture)
				{
					XElement rrPicture = new(RR + "Picture",
						new XElement(RR + "Status", galPicture.Element(GAL + "Status")?.Value ?? "173"));
					if (galPicture.Element(GAL + "Data") is XElement data)
						rrPicture.Add(new XElement(RR + "Data", data.Value));
					recipient.Add(rrPicture);
				}

				built.Add((recipient, email));
			}

			if (availabilityWindow is { } window)
			{
				XElement[] availabilities = await Task.WhenAll(
					built.Select(b => BuildAvailabilityAsync(context, b.Email, window, ct)));
				for (int i = 0; i < built.Count; i++)
					built[i].Recipient.Add(availabilities[i]);
			}

			recipients.AddRange(built.Select(b => b.Recipient));
		}

		if (recipients.Count == 0 && to.Contains('@'))
		{
			// Echo back a plain SMTP address as a resolved recipient.
			XElement echoed = new(RR + "Recipient",
				new XElement(RR + "Type", "2"),
				new XElement(RR + "DisplayName", to),
				new XElement(RR + "EmailAddress", to));
			if (availabilityWindow is { } window)
				echoed.Add(await BuildAvailabilityAsync(context, to, window, ct));
			recipients.Add(echoed);
		}

		// MS-ASCMD status: 1 = single match, 4 = no match, and for ambiguity 2 = more matches
		// than returned (the GAL truncated at the cap) vs 3 = all matches returned. Reporting
		// 1 for a many-way match makes the client pick one arbitrarily instead of prompting.
		string status = recipients.Count switch
		{
			0 => "4",
			1 => "1",
			_ => galHits >= maxAmbiguous ? "2" : "3"
		};
		return new XElement(RR + "Response",
			new XElement(RR + "To", to),
			new XElement(RR + "Status", status),
			new XElement(RR + "RecipientCount", recipients.Count.ToString()),
			recipients);
	}

	/// <summary>
	///   Per-recipient free/busy: the calendar store answers when it can (own calendar
	///   always; other principals only where the backend allows it) — otherwise status 163
	///   ("could not be retrieved") without failing the whole ResolveRecipients.
	/// </summary>
	private async Task<XElement> BuildAvailabilityAsync(
		EasContext context, string address, (DateTime StartUtc, DateTime EndUtc) window, CancellationToken ct)
	{
		try
		{
			if (context.Session.GetStoreForClass(EasClass.Calendar) is IFreeBusySource source)
			{
				IReadOnlyList<BusyPeriod>? periods = await source.GetBusyPeriodsAsync(
					address, window.StartUtc, window.EndUtc, ct);
				if (periods is not null)
					return new XElement(RR + "Availability",
						new XElement(RR + "Status", "1"),
						new XElement(RR + "MergedFreeBusy",
							MergedFreeBusy.Build(window.StartUtc, window.EndUtc, periods)));
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Free/busy lookup failed for a recipient");
		}

		return new XElement(RR + "Availability", new XElement(RR + "Status", "163"));
	}

	private static bool TryParseTime(string? value, out DateTime utc)
	{
		return DateTime.TryParse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
	}
}
