using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G3: <see cref="ImapMailBackend.ParseUid(uint, string, string)" /> must require the qualified
///   "&lt;uidvalidity&gt;:&lt;uid&gt;" form — an unqualified key must never be resolved against the
///   folder's current UidValidity. The live symptom (an unqualified key silently deleting/mutating
///   whatever now holds that UID) is proven red-first against a real IMAP backend in
///   <c>ImapMailBackendCorrectnessTests</c>; these are supplementary boundary-condition checks on
///   the extracted pure parser.
/// </summary>
public class ImapParseUidTests
{
	[Fact]
	public void QualifiedKey_MatchingValidity_Parses()
	{
		MailKit.UniqueId uid = ImapMailBackend.ParseUid(1000, "INBOX", "1000:42");
		Assert.Equal(1000u, uid.Validity);
		Assert.Equal(42u, uid.Id);
	}

	[Fact]
	public void UnqualifiedKey_IsRejected()
	{
		Assert.Throws<BackendItemNotFoundException>(() => ImapMailBackend.ParseUid(1000, "INBOX", "42"));
	}

	[Fact]
	public void QualifiedKey_StaleValidity_IsRejected()
	{
		Assert.Throws<BackendItemNotFoundException>(() => ImapMailBackend.ParseUid(1000, "INBOX", "999:42"));
	}

	[Fact]
	public void QualifiedKey_ZeroUid_IsRejected()
	{
		Assert.Throws<BackendItemNotFoundException>(() => ImapMailBackend.ParseUid(1000, "INBOX", "1000:0"));
	}
}
