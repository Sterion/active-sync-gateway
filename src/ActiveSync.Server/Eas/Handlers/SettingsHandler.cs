using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Sieve;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   Settings (MS-ASCMD 2.2.1.18): DeviceInformation set, UserInformation get, DevicePassword
///   recovery escrow (when the policy enables it), Oof backed by ManageSieve when configured
///   (stubbed otherwise).
/// </summary>
public sealed class SettingsHandler(
	IOptions<ActiveSyncOptions> options,
	LocalContentProtector protector,
	ILogger<SettingsHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace ST = EasNamespaces.Settings;

	public string Command => "Settings";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		XElement response = new(ST + "Settings", new XElement(ST + "Status", "1"));

		foreach (XElement section in request?.Root?.Elements() ?? [])
			switch (section.Name.LocalName)
			{
				case "DeviceInformation":
				{
					XElement? set = section.Element(ST + "Set");
					if (set is not null)
					{
						Dictionary<string, string> info = set.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
						await context.State.SaveDeviceInfoAsync(context.Device, JsonSerializer.Serialize(info), ct);
					}

					response.Add(new XElement(ST + "DeviceInformation",
						new XElement(ST + "Status", "1")));
					break;
				}
				case "UserInformation":
				{
					string? address = context.Session.MailAddress;
					XElement get = new(ST + "Get");
					if (address is not null)
						get.Add(new XElement(ST + "EmailAddresses",
							new XElement(ST + "SMTPAddress", address)));
					response.Add(new XElement(ST + "UserInformation",
						new XElement(ST + "Status", "1"),
						get));
					break;
				}
				case "Oof":
				{
					if (section.Element(ST + "Get") is not null)
						response.Add(await BuildOofGetAsync(context, ct));
					else if (section.Element(ST + "Set") is XElement set)
						response.Add(await ApplyOofSetAsync(context, set, ct));
					else
						response.Add(new XElement(ST + "Oof", new XElement(ST + "Status", "1")));
					break;
				}
				case "DevicePassword":
				{
					// Recovery-password escrow (MS-ASCMD 2.2.3.132): only accepted when the
					// policy offers it — Status 5 (invalid args) otherwise, so a client that
					// ignores PasswordRecoveryEnabled=0 gets an honest refusal instead of a
					// silent drop. Sealed with the master key, device-bound via the AAD.
					string? password = section.Element(ST + "Set")?.Element(ST + "Password")?.Value;
					PolicyOptions policy = options.Value.Policy;
					if (password is null || !policy.Enabled || !policy.PasswordRecoveryEnabled)
					{
						response.Add(new XElement(ST + "DevicePassword", new XElement(ST + "Status", "5")));
						break;
					}

					await context.State.SetRecoveryPasswordAsync(context.Device,
						protector.Protect(password, context.Device.UserName, "recovery:" + context.Device.DeviceId), ct);
					response.Add(new XElement(ST + "DevicePassword", new XElement(ST + "Status", "1")));
					break;
				}
			}

		await context.WriteResponseAsync(new XDocument(response));
	}

	/// <summary>
	///   Oof Get from the state database (the sieve script is derived output, never parsed
	///   back). No row = disabled. The single stored reply is reported for all three
	///   audience buckets — one reply for everyone, by design.
	/// </summary>
	private static async Task<XElement> BuildOofGetAsync(EasContext context, CancellationToken ct)
	{
		OofSetting? row = await context.State.GetOofAsync(context.Device.UserName, ct);
		XElement get = new(ST + "Get", new XElement(ST + "OofState", (row?.State ?? 0).ToString()));
		if (row is { State: 2, StartUtc: not null, EndUtc: not null })
		{
			get.Add(new XElement(ST + "StartTime", EasTime(row.StartUtc.Value)));
			get.Add(new XElement(ST + "EndTime", EasTime(row.EndUtc.Value)));
		}

		if (row is not null)
			foreach (string audience in (string[])
				["AppliesToInternal", "AppliesToExternalKnown", "AppliesToExternalUnknown"])
				get.Add(new XElement(ST + "OofMessage",
					new XElement(ST + audience),
					new XElement(ST + "Enabled", row.State == 0 ? "0" : "1"),
					new XElement(ST + "ReplyMessage", row.Message),
					new XElement(ST + "BodyType", row.BodyType)));

		return new XElement(ST + "Oof", new XElement(ST + "Status", "1"), get);
	}

	/// <summary>
	///   Oof Set: sieve first, then the database — a failed script upload must not leave the
	///   phone believing the auto-reply is armed. Without a Sieve backend the historical stub
	///   behavior remains (accept and ignore) so clients probing Oof see no change.
	/// </summary>
	private async Task<XElement> ApplyOofSetAsync(EasContext context, XElement set, CancellationToken ct)
	{
		IOofBackend? oof = context.Session.Oof;
		if (oof is null)
			return new XElement(ST + "Oof", new XElement(ST + "Status", "1"));

		OofSetting? row = await context.State.GetOofAsync(context.Device.UserName, ct);
		int state = int.TryParse(set.Element(ST + "OofState")?.Value, out int s) ? s : row?.State ?? 0;
		DateTime? start = ParseEasTime(set.Element(ST + "StartTime")?.Value) ?? row?.StartUtc;
		DateTime? end = ParseEasTime(set.Element(ST + "EndTime")?.Value) ?? row?.EndUtc;

		// One reply for everyone: prefer the internal-audience message (what phones show
		// first); any enabled message body wins over the stored one.
		XElement? messageElement = set.Elements(ST + "OofMessage")
			.OrderByDescending(m => m.Element(ST + "AppliesToInternal") is not null)
			.FirstOrDefault(m => m.Element(ST + "ReplyMessage") is not null);
		string message = messageElement?.Element(ST + "ReplyMessage")?.Value ?? row?.Message ?? "";
		string bodyType = messageElement?.Element(ST + "BodyType")?.Value ?? row?.BodyType ?? "Text";

		if (state == 2 && (start is null || end is null))
			return new XElement(ST + "Oof", new XElement(ST + "Status", "6")); // conflicting arguments

		try
		{
			string? previousActive = row?.PreviousActiveScript;
			if (state == 0)
			{
				// Only touch the sieve server when the gateway actually armed something.
				if (row is { State: not 0 })
					await oof.DeactivateAsync(previousActive ?? "", ct);
				previousActive = null;
			}
			else
			{
				string script = SieveVacationScript.Build(
					message, state == 2 ? start : null, state == 2 ? end : null);
				string wasActive = await oof.ActivateAsync(script, ct);
				// Remember what to restore — unless it is our own script (re-arm case).
				if (wasActive != SieveVacationScript.ScriptName)
					previousActive = wasActive;
			}

			await context.State.SaveOofAsync(
				context.Device.UserName, state, start, end, message, bodyType, previousActive, ct);
			return new XElement(ST + "Oof", new XElement(ST + "Status", "1"));
		}
		catch (BackendException ex)
		{
			// MS-ASCMD Settings status 4: server unavailable — the phone shows a retry hint.
			logger.LogWarning(ex, "Oof Set failed against the sieve server for {User}",
				LogText.Clean(context.Device.UserName, 128));
			return new XElement(ST + "Oof", new XElement(ST + "Status", "4"));
		}
	}

	private static string EasTime(DateTime utc)
	{
		return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
	}

	private static DateTime? ParseEasTime(string? value)
	{
		return DateTime.TryParse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
			? parsed
			: null;
	}
}
