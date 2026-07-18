using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Settings for the "caldav"/"carddav" providers (Calendar/Tasks/Contacts roles).
///   TLS-trust knobs come from <see cref="NetworkBackendOptions" />.
/// </summary>
public sealed class DavServerOptions : NetworkBackendOptions
{
	/// <summary>Base URL of the CalDAV/CardDAV server, e.g. https://dav.example.com</summary>
	public string BaseUrl { get; set; } = "";

	/// <summary>
	///   Path template for the user's collection home set. {user} and {localpart} are substituted.
	///   Example (Radicale/Baikal style): "/{user}/".
	///   When empty, the home set is discovered via .well-known and current-user-principal.
	/// </summary>
	public string? HomeSetPath { get; set; }

	/// <summary>
	///   CalDav only: name of the VTODO (tasks) collection in the calendar home set. When a
	///   collection with this display name or path segment exists, it is exposed to clients
	///   as the ActiveSync Tasks folder (Axigen ships one named "Tasks"). Empty/null
	///   disables CalDAV task detection; without it tasks are stored in the gateway
	///   database instead.
	/// </summary>
	public string? TaskFolder { get; set; } = "Tasks";

	/// <summary>
	///   CalDav only — event attachments for EAS 16.x clients: "Auto" (enabled, 1 MiB per
	///   attachment), "On" (enabled, 16 MiB) or "Off". Attachments are stored INLINE in the
	///   event (base64 ATTACH property) so they work against any CalDAV server and the
	///   local store alike — the size cap exists because inline blobs bloat the events on
	///   the DAV server.
	/// </summary>
	public string CalendarAttachments { get; set; } = "Auto";

	/// <summary>
	///   CalDav only (CardDav ignores it for now) — extra collection hrefs synced as
	///   additional calendar folders on every device: absolute paths ("/dav/cal/team/") or
	///   same-host URLs, each optionally suffixed "|ro" for gateway-enforced read-only.
	///   Collections the DAV server refuses (403/404) are skipped with a warning, never
	///   breaking folder sync. Runtime per-user grants via `eas share` add to this list.
	/// </summary>
	public List<string>? SharedCollections { get; set; }

	/// <summary>
	///   CalDav only — iMIP invitation mails (METHOD:REQUEST/CANCEL) when the user creates,
	///   updates or cancels a meeting as its organizer: "Auto" (send unless the server
	///   advertises an RFC 6638 schedule outbox — a scheduling server invites on its own,
	///   and double invites are worse than none), "On" (always send) or "Off" (never).
	/// </summary>
	public string SendInvitations { get; set; } = "Auto";
}
