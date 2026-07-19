using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Core.Options;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   Provision (MS-ASPROV): hands out the configured device security policy (an empty
///   document when ActiveSync:Policy is disabled — the historical no-op behavior) via the
///   standard two-phase key handshake. No RemoteWipe support by design.
/// </summary>
public sealed class ProvisionHandler(IOptionsSnapshot<ActiveSyncOptions> options) : IEasCommandHandler
{
	private const string PolicyType = "MS-EAS-Provisioning-WBXML";
	private static readonly XNamespace PV = EasNamespaces.Provision;
	private static readonly XNamespace ST = EasNamespaces.Settings;

	public string Command => "Provision";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();

		// Account-only remote wipe (16.1). While pending, Provision delivers the directive
		// (all other commands already 449 into here); the acknowledgment marks the wipe done
		// and auto-blocks the partnership. There is deliberately no full-device wipe path.
		if (context.Device.PendingAccountWipe)
		{
			if (request?.Root?.Element(PV + "AccountOnlyRemoteWipe")?
				    .Element(PV + "Status")?.Value == "1")
			{
				await context.State.CompleteAccountWipeAsync(context.Device, ct);
				await context.WriteResponseAsync(new XDocument(new XElement(PV + "Provision",
					new XElement(PV + "Status", "1"))));
				return;
			}

			await context.WriteResponseAsync(new XDocument(new XElement(PV + "Provision",
				new XElement(PV + "Status", "1"),
				new XElement(PV + "AccountOnlyRemoteWipe"))));
			return;
		}

		XElement? policy = request?.Root?.Element(PV + "Policies")?.Element(PV + "Policy");
		string? clientPolicyKey = policy?.Element(PV + "PolicyKey")?.Value;

		// DeviceInformation may be piggybacked on Provision (14.x).
		XElement? deviceInfo = request?.Root?.Element(ST + "DeviceInformation")?.Element(ST + "Set");
		if (deviceInfo is not null)
		{
			Dictionary<string, string> info = deviceInfo.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
			await context.State.SaveDeviceInfoAsync(context.Device, JsonSerializer.Serialize(info), ct);
		}

		XElement policyResponse;
		if (clientPolicyKey is null)
		{
			// Phase 1: hand out a temporary key and the configured policy document.
			uint tempKey = RandomKey();
			await context.State.SetPolicyKeyAsync(context.Device, tempKey, null, ct);
			policyResponse = new XElement(PV + "Policy",
				new XElement(PV + "PolicyType", PolicyType),
				new XElement(PV + "Status", "1"),
				new XElement(PV + "PolicyKey", tempKey.ToString()),
				new XElement(PV + "Data",
					PolicyDocument.Build(options.Value.Policy)));
		}
		else
		{
			// Phase 2: the device acknowledged the policy — issue the final key and record
			// WHICH policy it acknowledged, so a config change forces a re-provision (449).
			uint finalKey = RandomKey();
			await context.State.SetPolicyKeyAsync(
				context.Device, finalKey, PolicyDocument.Hash(options.Value.Policy), ct);
			policyResponse = new XElement(PV + "Policy",
				new XElement(PV + "PolicyType", PolicyType),
				new XElement(PV + "Status", "1"),
				new XElement(PV + "PolicyKey", finalKey.ToString()));
		}

		XElement response = new(PV + "Provision",
			new XElement(PV + "Status", "1"),
			new XElement(PV + "Policies", policyResponse));
		if (deviceInfo is not null)
			response.AddFirst(new XElement(ST + "DeviceInformation", new XElement(ST + "Status", "1")));

		await context.WriteResponseAsync(new XDocument(response));
	}

	private static uint RandomKey()
	{
		Span<byte> bytes = stackalloc byte[4];
		RandomNumberGenerator.Fill(bytes);
		uint value = BitConverter.ToUInt32(bytes);
		// 0 is reserved: the state store uses PolicyKey 0 to mean "no partnership yet", so a
		// freshly issued key must never be 0.
		return value == 0 ? 1u : value;
	}
}
