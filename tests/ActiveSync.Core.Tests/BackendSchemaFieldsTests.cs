using ActiveSync.Backends.Common;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D31 — the derived default for the standard mail ports (Host/Port unset, Security unset) is
///   opportunistic STARTTLS (StartTlsWhenAvailable), which downgrades to cleartext silently when
///   a server's greeting omits the STARTTLS capability (an on-path attacker can strip it). The
///   Security field's help text is the one place an operator reading the settings form would learn
///   that "unset" does not mean "TLS is required" — it must say so.
/// </summary>
public class BackendSchemaFieldsTests
{
	[Fact]
	public void SecurityField_HelpText_WarnsThatUnsetIsNotARequiredTlsUpgrade()
	{
		BackendConfigField security = BackendSchemaFields.MailConnection(143)
			.Single(f => f.Name == "Security");

		Assert.Contains("unset", security.Help, StringComparison.OrdinalIgnoreCase);
		// The specific risk: leaving Security unset does not guarantee an upgrade to TLS —
		// a stripped STARTTLS capability silently falls back to cleartext.
		Assert.True(
			security.Help.Contains("cleartext", StringComparison.OrdinalIgnoreCase) ||
			security.Help.Contains("downgrad", StringComparison.OrdinalIgnoreCase),
			$"Security field help does not warn about the opportunistic-STARTTLS downgrade risk: \"{security.Help}\"");
	}
}
