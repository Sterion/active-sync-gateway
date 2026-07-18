using System.Xml.Linq;

namespace ActiveSync.Protocol.Wbxml;

/// <summary>
///   XML namespaces for the EAS code pages, named exactly as in MS-ASWBXML so the
///   decoded XML matches the plain-text examples in the MS-AS* specifications.
/// </summary>
public static class EasNamespaces
{
	public static readonly XNamespace AirSync = "AirSync";
	public static readonly XNamespace Contacts = "Contacts";
	public static readonly XNamespace Email = "Email";
	public static readonly XNamespace AirNotify = "AirNotify";
	public static readonly XNamespace Calendar = "Calendar";
	public static readonly XNamespace Move = "Move";
	public static readonly XNamespace GetItemEstimate = "GetItemEstimate";
	public static readonly XNamespace FolderHierarchy = "FolderHierarchy";
	public static readonly XNamespace MeetingResponse = "MeetingResponse";
	public static readonly XNamespace Tasks = "Tasks";
	public static readonly XNamespace ResolveRecipients = "ResolveRecipients";
	public static readonly XNamespace ValidateCert = "ValidateCert";
	public static readonly XNamespace Contacts2 = "Contacts2";
	public static readonly XNamespace Ping = "Ping";
	public static readonly XNamespace Provision = "Provision";
	public static readonly XNamespace Search = "Search";
	public static readonly XNamespace Gal = "Gal";
	public static readonly XNamespace AirSyncBase = "AirSyncBase";
	public static readonly XNamespace Settings = "Settings";
	public static readonly XNamespace DocumentLibrary = "DocumentLibrary";
	public static readonly XNamespace ItemOperations = "ItemOperations";
	public static readonly XNamespace ComposeMail = "ComposeMail";
	public static readonly XNamespace Email2 = "Email2";
	public static readonly XNamespace Notes = "Notes";
	public static readonly XNamespace RightsManagement = "RightsManagement";
	public static readonly XNamespace Find = "Find";

	/// <summary>Internal marker namespace used to flag OPAQUE-encoded element content (base64 in the XML).</summary>
	public static readonly XNamespace WbxmlInternal = "urn:activesync:wbxml";

	public static readonly XName OpaqueAttribute = WbxmlInternal + "opaque";
}
