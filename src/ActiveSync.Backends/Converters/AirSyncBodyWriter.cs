using System.Xml.Linq;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Backends.Converters;

/// <summary>
///   Builds the AirSyncBase &lt;Body&gt; element shared by the calendar/task/note/contact
///   converters (plain-text, Type 1). Callers supply the full-text byte size (which the client
///   uses to decide whether to re-fetch untruncated) and the possibly-truncated data.
/// </summary>
internal static class AirSyncBodyWriter
{
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static XElement Build(long estimatedDataSize, bool truncated, string data)
	{
		return new XElement(AirSyncBase + "Body",
			new XElement(AirSyncBase + "Type", "1"),
			new XElement(AirSyncBase + "EstimatedDataSize", estimatedDataSize.ToString()),
			new XElement(AirSyncBase + "Truncated", truncated ? "1" : "0"),
			new XElement(AirSyncBase + "Data", data));
	}
}
