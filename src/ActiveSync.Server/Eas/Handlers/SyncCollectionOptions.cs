using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   The effective per-collection Sync options (FilterType + body preference), with the
///   parse-from-request and persist-to-state helpers alongside. Extracted from
///   <see cref="SyncHandler" /> so option handling lives in one place.
/// </summary>
internal sealed record SyncCollectionOptions(int FilterType, int BodyType, long? TruncationSize, int MimeSupport = 0)
{
	public static readonly SyncCollectionOptions Default = new(0, 2, 200 * 1024);

	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	/// <summary>
	///   The effective options for a collection round: a client-supplied &lt;Options&gt; wins,
	///   else the options persisted on the collection state, else the defaults.
	/// </summary>
	public static SyncCollectionOptions Resolve(XElement? optionsElement, CollectionState state)
	{
		SyncCollectionOptions? options = Parse(optionsElement);
		if (options is null && state.OptionsJson is not null)
			options = JsonSerializer.Deserialize<SyncCollectionOptions>(state.OptionsJson);
		return options ?? Default;
	}

	/// <summary>JSON form persisted on <see cref="CollectionState.OptionsJson" />.</summary>
	public string ToJson()
	{
		return JsonSerializer.Serialize(this);
	}

	private static SyncCollectionOptions? Parse(XElement? optionsElement)
	{
		if (optionsElement is null)
			return null;
		int filterType = int.TryParse(optionsElement.Element(AS + "FilterType")?.Value, out int ft) ? ft : 0;

		// MS-ASCMD MIMESupport: 0 = never send MIME, 1 = S/MIME messages only, 2 = always send MIME.
		int mimeSupport = int.TryParse(optionsElement.Element(AS + "MIMESupport")?.Value, out int ms) ? ms : 0;

		// AirSyncBase body Type codes (MS-ASAIRS): 1 = plain, 2 = HTML, 4 = MIME. When a
		// client offers several, prefer the richest we render well: HTML (2) > plain (1) >
		// whatever else it listed first.
		int bodyType = 2;
		long? truncation = 200 * 1024;
		var preferences = optionsElement.Elements(ASB + "BodyPreference")
			.Select(p => new
			{
				Type = int.TryParse(p.Element(ASB + "Type")?.Value, out int t) ? t : 1,
				Truncation = long.TryParse(p.Element(ASB + "TruncationSize")?.Value, out long tr)
					? (long?)tr
					: null
			})
			.ToList();
		if (preferences.Count > 0)
		{
			// F6: honour MIMESupport. When the client offers a Type-4 (MIME) BodyPreference and asks
			// for MIME (always, or S/MIME-only), send raw MIME rather than downgrading to the HTML
			// ladder — otherwise S/MIME signed/encrypted mail can never be verified or decrypted on
			// device. Absent a MIME preference, fall back to the richest we render well.
			var mime = preferences.FirstOrDefault(p => p.Type == 4);
			var chosen = mimeSupport >= 1 && mime is not null
				? mime
				: preferences.FirstOrDefault(p => p.Type == 2)
				  ?? preferences.FirstOrDefault(p => p.Type == 1)
				  ?? preferences[0];
			bodyType = chosen.Type;
			truncation = chosen.Truncation;
		}

		return new SyncCollectionOptions(filterType, bodyType, truncation, mimeSupport);
	}
}
