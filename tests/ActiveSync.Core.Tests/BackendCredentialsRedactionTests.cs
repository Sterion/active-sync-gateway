using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   <see cref="BackendCredentials" /> is a published-contract record, so the
///   compiler-synthesized <c>ToString()</c> prints every member — including the plaintext
///   password — into any log line, exception message or debugger view that stringifies it
///   (directly, or nested via <see cref="ResolvedRole" /> / <see cref="BackendConnectionContext" />).
///   The password must never appear in the rendered form.
/// </summary>
public class BackendCredentialsRedactionTests
{
	private const string Secret = "hunter2-plaintext";

	[Fact]
	public void ToString_DoesNotLeakPassword()
	{
		BackendCredentials credentials = new() { UserName = "alice@example.com", Password = Secret };
		string rendered = credentials.ToString();
		Assert.DoesNotContain(Secret, rendered);
		Assert.Contains("alice@example.com", rendered); // the login stays visible for diagnostics
		// The mask itself, not just the absence of the secret: a PrintMembers override dropped in a
		// mechanical record rewrite (positional -> init-only properties is exactly such a rewrite)
		// would still satisfy "does not contain the password" if the member vanished entirely,
		// while quietly removing the redaction this type exists to guarantee.
		Assert.Contains("Password = ***", rendered);
	}

	[Fact]
	public void ToString_DoesNotLeakPassword_WhenNestedInResolvedRole()
	{
		ResolvedRole role = new()
		{
			Role = BackendRole.MailStore,
			ProviderName = "imap",
			Settings = ProviderSettings.Empty,
			Credentials = new BackendCredentials { UserName = "alice", Password = Secret }
		};
		Assert.DoesNotContain(Secret, role.ToString());
	}

	[Fact]
	public void ToString_DoesNotLeakPassword_WhenNestedInConnectionContext()
	{
		BackendConnectionContext context = new()
		{
			GatewayCredentials = new BackendCredentials { UserName = "alice", Password = Secret },
			GatewayUserId = 1,
			Roles = [],
			SharedCollections = []
		};
		Assert.DoesNotContain(Secret, context.ToString());
	}
}
