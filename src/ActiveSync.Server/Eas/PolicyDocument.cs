using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Core.Options;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Builds the MS-ASPROV EASProvisionDoc from the configured policy, and hashes it so the
///   endpoint can tell whether a device acknowledged the CURRENT policy (a config change
///   changes the hash, which forces a re-provision via HTTP 449). Element order follows the
///   MS-ASPROV schema; nullable knobs are omitted so the device default applies, and the
///   password sub-knobs are only emitted when a device password is required at all.
/// </summary>
public static class PolicyDocument
{
	private static readonly XNamespace PV = EasNamespaces.Provision;

	public static XElement Build(PolicyOptions policy)
	{
		XElement doc = new(PV + "EASProvisionDoc");
		if (!policy.Enabled)
			return doc;

		doc.Add(Flag("DevicePasswordEnabled", policy.DevicePasswordEnabled));
		if (policy.DevicePasswordEnabled)
		{
			doc.Add(Flag("AlphanumericDevicePasswordRequired", policy.AlphanumericDevicePasswordRequired));
			doc.Add(Flag("PasswordRecoveryEnabled", policy.PasswordRecoveryEnabled));
			AddValue(doc, "MinDevicePasswordLength", policy.MinDevicePasswordLength);
			AddValue(doc, "MaxInactivityTimeDeviceLock", policy.MaxInactivityTimeDeviceLock);
			AddValue(doc, "MaxDevicePasswordFailedAttempts", policy.MaxDevicePasswordFailedAttempts);
			doc.Add(Flag("AllowSimpleDevicePassword", policy.AllowSimpleDevicePassword));
			AddValue(doc, "DevicePasswordExpiration", policy.DevicePasswordExpiration);
			AddValue(doc, "DevicePasswordHistory", policy.DevicePasswordHistory);
			AddValue(doc, "MinDevicePasswordComplexCharacters", policy.MinDevicePasswordComplexCharacters);
		}

		AddValue(doc, "MaxAttachmentSize", policy.MaxAttachmentSize);
		doc.Add(Flag("RequireDeviceEncryption", policy.RequireDeviceEncryption));
		return doc;
	}

	/// <summary>Hex SHA-256 of the document a device must have acknowledged to be current.</summary>
	public static string Hash(PolicyOptions policy)
	{
		string canonical = Build(policy).ToString(SaveOptions.DisableFormatting);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
	}

	private static XElement Flag(string name, bool value)
	{
		return new XElement(PV + name, value ? "1" : "0");
	}

	private static void AddValue(XElement doc, string name, int? value)
	{
		if (value is not null)
			doc.Add(new XElement(PV + name, value.Value.ToString()));
	}
}
