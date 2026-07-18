using System.Xml.Linq;
using ActiveSync.Protocol;

namespace ActiveSync.Core.Backend;

public sealed record BackendCredentials(string UserName, string Password);

/// <summary>A folder/collection as reported by a backend store.</summary>
public sealed record BackendFolder(
	string BackendKey, // stable backend identifier, e.g. "imap:INBOX/Sub" or "caldav:/user/cal1/"
	string DisplayName,
	string? ParentBackendKey,
	int EasType, // EasFolderType value
	string EasClass); // EasClass value

/// <summary>Client body preference from Sync/ItemOperations options (AirSyncBase BodyPreference).</summary>
/// <summary>
///   The client's body preference plus the negotiated-protocol flag converters need:
///   <see cref="Eas16" /> selects the 16.x shapes (airsyncbase:Location instead of
///   calendar:Location, draft/attachment metadata) without threading a version type
///   through every store signature.
/// </summary>
public sealed record BodyPreference(int Type, long? TruncationSize, bool AllOrNone, bool Eas16 = false)
{
	public static readonly BodyPreference PlainText = new(1, 32 * 1024, false);
}

/// <summary>Server-side filter for a collection (from AirSync FilterType).</summary>
public sealed record ContentFilter(DateTime? SinceUtc)
{
	public static readonly ContentFilter All = new((DateTime?)null);

	public static ContentFilter FromMailFilterType(int filterType)
	{
		return filterType switch
		{
			1 => new ContentFilter(DateTime.UtcNow.AddDays(-1)),
			2 => new ContentFilter(DateTime.UtcNow.AddDays(-3)),
			3 => new ContentFilter(DateTime.UtcNow.AddDays(-7)),
			4 => new ContentFilter(DateTime.UtcNow.AddDays(-14)),
			5 => new ContentFilter(DateTime.UtcNow.AddMonths(-1)),
			6 => new ContentFilter(DateTime.UtcNow.AddMonths(-3)),
			7 => new ContentFilter(DateTime.UtcNow.AddMonths(-6)),
			_ => All
		};
	}

	public static ContentFilter FromCalendarFilterType(int filterType)
	{
		return filterType switch
		{
			4 => new ContentFilter(DateTime.UtcNow.AddDays(-14)),
			5 => new ContentFilter(DateTime.UtcNow.AddMonths(-1)),
			6 => new ContentFilter(DateTime.UtcNow.AddMonths(-3)),
			7 => new ContentFilter(DateTime.UtcNow.AddMonths(-6)),
			_ => All
		};
	}

	/// <summary>
	///   Picks the filter window appropriate to a store's content class: mail and calendar
	///   have their own FilterType→date-window mappings; everything else (contacts, tasks,
	///   notes) is never date-filtered.
	/// </summary>
	public static ContentFilter ForClass(string easClass, int filterType)
	{
		return easClass switch
		{
			EasClass.Email => FromMailFilterType(filterType),
			EasClass.Calendar => FromCalendarFilterType(filterType),
			_ => All
		};
	}
}

/// <summary>Content of a fetched item, as EAS ApplicationData child elements.</summary>
public sealed record BackendItem(IReadOnlyList<XElement> ApplicationData);

/// <summary>An attachment payload fetched from a backend.</summary>
public sealed record BackendAttachment(string ContentType, byte[] Content);

public sealed class BackendException : Exception
{
	public BackendException(string message) : base(message)
	{
	}

	public BackendException(string message, Exception inner) : base(message, inner)
	{
	}
}

/// <summary>Thrown when the referenced backend object no longer exists.</summary>
public sealed class BackendItemNotFoundException(string message) : Exception(message);
