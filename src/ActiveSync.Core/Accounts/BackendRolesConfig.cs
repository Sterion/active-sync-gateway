using ActiveSync.Core.Backend;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Accounts;

/// <summary>One globally assigned role: its provider name and its raw settings section.</summary>
public sealed record RoleAssignment(BackendRole Role, string ProviderName, ProviderSettings Settings);

/// <summary>
///   The parsed ActiveSync:Backends section: one sub-section per role, each carrying the
///   host-reserved "Provider" key plus provider-owned settings the host never binds.
///   MailStore and MailSubmit are mandatory; absent content roles fall back to the "local"
///   provider; an absent Oof role means the feature is off. Parsed once at startup —
///   configuration reloads are not supported (restart to apply).
/// </summary>
public sealed class BackendRolesConfig
{
	/// <summary>The one key inside a role section that belongs to the host, not the provider.</summary>
	public const string ProviderKey = "Provider";

	private BackendRolesConfig(IReadOnlyDictionary<BackendRole, RoleAssignment> assignments)
	{
		Assignments = assignments;
	}

	/// <summary>Every role that exists for this deployment (Oof only when configured).</summary>
	public IReadOnlyDictionary<BackendRole, RoleAssignment> Assignments { get; }

	public static BackendRolesConfig Load(IConfiguration configuration, IList<string> failures)
	{
		IConfigurationSection root = configuration.GetSection("ActiveSync:Backends");
		Dictionary<BackendRole, RoleAssignment> assignments = new();

		foreach (IConfigurationSection section in root.GetChildren())
		{
			if (!Enum.TryParse(section.Key, true, out BackendRole role))
			{
				failures.Add(
					$"ActiveSync:Backends:{section.Key} is not a backend role " +
					$"(roles: {string.Join(", ", Enum.GetNames<BackendRole>())}).");
				continue;
			}

			string? provider = section[ProviderKey];
			if (string.IsNullOrWhiteSpace(provider))
			{
				failures.Add($"ActiveSync:Backends:{section.Key}:Provider is required.");
				continue;
			}

			if (!assignments.TryAdd(role, new RoleAssignment(role, provider.Trim(), new ProviderSettings(section))))
				failures.Add($"ActiveSync:Backends declares the {role} role twice (keys are case-insensitive).");
		}

		foreach (BackendRole mandatory in (BackendRole[])[BackendRole.MailStore, BackendRole.MailSubmit])
			if (!assignments.ContainsKey(mandatory))
				failures.Add(
					$"ActiveSync:Backends:{mandatory} is required — this gateway cannot run without mail access " +
					$"(e.g. {{ \"Provider\": \"{(mandatory == BackendRole.MailStore ? "imap" : "smtp")}\", \"Host\": ... }}).");

		// Content classes without a configured backend are served from the gateway database;
		// Notes is always local unless explicitly assigned. Absent Oof = feature off.
		foreach (BackendRole content in (BackendRole[])
			[BackendRole.Calendar, BackendRole.Tasks, BackendRole.Contacts, BackendRole.Notes])
			assignments.TryAdd(content, new RoleAssignment(content, "local", ProviderSettings.Empty));

		return new BackendRolesConfig(assignments);
	}
}
