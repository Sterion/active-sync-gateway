using System.Xml.Linq;

namespace ActiveSync.Backends.Dav;

/// <summary>Well-known WebDAV/CalDAV/CardDAV XML namespaces used across the DAV stores.</summary>
public static class DavNs
{
	public static readonly XNamespace D = "DAV:";
	public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
	public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
	public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
}
