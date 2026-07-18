using System.Xml.Linq;
using ActiveSync.Core.Options;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The MS-ASPROV policy document builder: shape by configuration, omission rules for
///   nullable knobs and password sub-knobs, and the acknowledgment hash that drives the
///   449 re-provision when the configured policy changes.
/// </summary>
public sealed class PolicyDocumentTests
{
	private static readonly XNamespace PV = EasNamespaces.Provision;

	[Fact]
	public void Disabled_YieldsEmptyDocument()
	{
		XElement doc = PolicyDocument.Build(new PolicyOptions());
		Assert.Equal(PV + "EASProvisionDoc", doc.Name);
		Assert.Empty(doc.Elements());
	}

	[Fact]
	public void Enabled_Defaults_EmitOnlyTheAlwaysOnFlags()
	{
		XElement doc = PolicyDocument.Build(new PolicyOptions { Enabled = true });
		Assert.Equal("0", doc.Element(PV + "DevicePasswordEnabled")?.Value);
		Assert.Equal("0", doc.Element(PV + "RequireDeviceEncryption")?.Value);
		// No password requirement → none of the password sub-knobs are emitted.
		Assert.Null(doc.Element(PV + "MinDevicePasswordLength"));
		Assert.Null(doc.Element(PV + "AllowSimpleDevicePassword"));
		Assert.Null(doc.Element(PV + "MaxAttachmentSize"));
	}

	[Fact]
	public void Enabled_FullPasswordPolicy_EmitsConfiguredElements()
	{
		PolicyOptions policy = new()
		{
			Enabled = true,
			DevicePasswordEnabled = true,
			AlphanumericDevicePasswordRequired = true,
			AllowSimpleDevicePassword = false,
			MinDevicePasswordLength = 6,
			MinDevicePasswordComplexCharacters = 2,
			MaxInactivityTimeDeviceLock = 300,
			MaxDevicePasswordFailedAttempts = 8,
			DevicePasswordExpiration = 90,
			DevicePasswordHistory = 3,
			RequireDeviceEncryption = true,
			MaxAttachmentSize = 10_485_760,
			PasswordRecoveryEnabled = true
		};

		XElement doc = PolicyDocument.Build(policy);
		Assert.Equal("1", doc.Element(PV + "DevicePasswordEnabled")?.Value);
		Assert.Equal("1", doc.Element(PV + "AlphanumericDevicePasswordRequired")?.Value);
		Assert.Equal("1", doc.Element(PV + "PasswordRecoveryEnabled")?.Value);
		Assert.Equal("0", doc.Element(PV + "AllowSimpleDevicePassword")?.Value);
		Assert.Equal("6", doc.Element(PV + "MinDevicePasswordLength")?.Value);
		Assert.Equal("2", doc.Element(PV + "MinDevicePasswordComplexCharacters")?.Value);
		Assert.Equal("300", doc.Element(PV + "MaxInactivityTimeDeviceLock")?.Value);
		Assert.Equal("8", doc.Element(PV + "MaxDevicePasswordFailedAttempts")?.Value);
		Assert.Equal("90", doc.Element(PV + "DevicePasswordExpiration")?.Value);
		Assert.Equal("3", doc.Element(PV + "DevicePasswordHistory")?.Value);
		Assert.Equal("1", doc.Element(PV + "RequireDeviceEncryption")?.Value);
		Assert.Equal("10485760", doc.Element(PV + "MaxAttachmentSize")?.Value);
	}

	[Fact]
	public void UnsetNullableKnobs_AreOmitted_NotEmittedAsZero()
	{
		PolicyOptions policy = new() { Enabled = true, DevicePasswordEnabled = true };
		XElement doc = PolicyDocument.Build(policy);
		Assert.Null(doc.Element(PV + "MinDevicePasswordLength"));
		Assert.Null(doc.Element(PV + "MaxInactivityTimeDeviceLock"));
		Assert.Null(doc.Element(PV + "DevicePasswordExpiration"));
		// The boolean sub-knobs ARE emitted once a password is required.
		Assert.Equal("1", doc.Element(PV + "AllowSimpleDevicePassword")?.Value);
	}

	[Fact]
	public void Hash_IsStableForEqualConfig_AndChangesWithAnyKnob()
	{
		PolicyOptions a = new() { Enabled = true, DevicePasswordEnabled = true, MinDevicePasswordLength = 6 };
		PolicyOptions same = new() { Enabled = true, DevicePasswordEnabled = true, MinDevicePasswordLength = 6 };
		PolicyOptions different = new() { Enabled = true, DevicePasswordEnabled = true, MinDevicePasswordLength = 8 };

		Assert.Equal(PolicyDocument.Hash(a), PolicyDocument.Hash(same));
		Assert.NotEqual(PolicyDocument.Hash(a), PolicyDocument.Hash(different));
	}
}
