using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   K56: <see cref="BackendCredentials" /> is a published-contract record, so the
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
		BackendCredentials credentials = new("alice@example.com", Secret);
		string rendered = credentials.ToString();
		Assert.DoesNotContain(Secret, rendered);
		Assert.Contains("alice@example.com", rendered); // the login stays visible for diagnostics
	}

	[Fact]
	public void ToString_DoesNotLeakPassword_WhenNestedInResolvedRole()
	{
		ResolvedRole role = new(
			BackendRole.MailStore, "imap", ProviderSettings.Empty,
			new BackendCredentials("alice", Secret));
		Assert.DoesNotContain(Secret, role.ToString());
	}

	[Fact]
	public void ToString_DoesNotLeakPassword_WhenNestedInConnectionContext()
	{
		BackendConnectionContext context = new(
			new BackendCredentials("alice", Secret), null, [], []);
		Assert.DoesNotContain(Secret, context.ToString());
	}
}
