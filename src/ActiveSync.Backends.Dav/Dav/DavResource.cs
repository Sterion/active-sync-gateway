using System.Xml.Linq;

namespace ActiveSync.Backends.Dav;

/// <summary>One <c>&lt;response&gt;</c> from a multistatus: its href plus the 200-status propstat.</summary>
public sealed record DavResource(string Href, XElement Propstat);
