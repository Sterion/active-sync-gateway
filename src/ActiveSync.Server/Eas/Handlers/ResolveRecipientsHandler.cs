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

		List<XElement> responses = new();

		foreach (string to in tos)
		{
			List<XElement> recipients = new();
			if (context.Session.Contacts is not null)
			{
				IReadOnlyList<IReadOnlyList<XElement>> hits =
					await context.Session.Contacts.SearchGalAsync(to, 10, photos, ct);
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

					if (availabilityWindow is { } window)
						recipient.Add(await BuildAvailabilityAsync(context, email, window, ct));

					recipients.Add(recipient);
				}
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

			responses.Add(new XElement(RR + "Response",
				new XElement(RR + "To", to),
				new XElement(RR + "Status", recipients.Count > 0 ? "1" : "4"),
				new XElement(RR + "RecipientCount", recipients.Count.ToString()),
				recipients));
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(RR + "ResolveRecipients",
				new XElement(RR + "Status", "1"),
				responses)));
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
