using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B7 (item 37): <see cref="BackendSettingsValidation.CaPath" /> memoizes the exists+PEM-parse of a
///   CA file keyed on (path, last-write-time, length) so validating N users that share the same
///   configured CA path no longer re-reads and re-parses the file N times. These are COVERAGE tests:
///   the memoization is behaviour-preserving, so they assert the observable result is unchanged
///   (valid file → no failure, garbage → failure) AND that the cache invalidates when the file's
///   content changes, which is the property a naive path-only cache would break.
/// </summary>
public sealed class BackendSettingsValidationTests : IDisposable
{
	private readonly List<string> _temp = [];

	public void Dispose()
	{
		foreach (string path in _temp)
			try { File.Delete(path); } catch { /* best effort */ }
	}

	private string NewTempFile(string contents)
	{
		string path = Path.Combine(Path.GetTempPath(), $"as-ca-{Guid.NewGuid():N}.pem");
		File.WriteAllText(path, contents);
		_temp.Add(path);
		return path;
	}

	private static string SelfSignedPem()
	{
		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new("CN=test-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using X509Certificate2 cert = request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
		return cert.ExportCertificatePem() + "\n";
	}

	[Fact]
	public void CaPath_UnsetOrMissing_BehavesAsBefore()
	{
		List<string> failures = [];
		BackendSettingsValidation.CaPath(null, "ctx", failures);
		BackendSettingsValidation.CaPath("   ", "ctx", failures);
		Assert.Empty(failures);

		BackendSettingsValidation.CaPath(
			Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.pem"), "ctx", failures);
		Assert.Single(failures);
		Assert.Contains("does not exist", failures[0]);
	}

	[Fact]
	public void CaPath_ValidPem_NoFailure_AndRepeatedCallsAgree()
	{
		string path = NewTempFile(SelfSignedPem());

		// Many callers (one per user × role) validate the same path — all must agree, and the result
		// is context-prefixed exactly as before.
		for (int i = 0; i < 5; i++)
		{
			List<string> failures = [];
			BackendSettingsValidation.CaPath(path, $"ctx{i}", failures);
			Assert.Empty(failures);
		}
	}

	[Fact]
	public void CaPath_Garbage_ReportsFailure_WithContextPrefix()
	{
		string path = NewTempFile("not a certificate");
		List<string> failures = [];
		BackendSettingsValidation.CaPath(path, "MyContext", failures);
		Assert.Single(failures);
		Assert.StartsWith("MyContext: ", failures[0]);
		Assert.Contains("CaCertificatePath", failures[0]);
	}

	[Fact]
	public void CaPath_CacheInvalidates_WhenFileContentChanges()
	{
		// A path-only cache would keep reporting the first verdict forever. Prove the memo keys on the
		// file's content stamp: a good file that is later replaced with garbage must start failing.
		string path = NewTempFile(SelfSignedPem());
		List<string> first = [];
		BackendSettingsValidation.CaPath(path, "ctx", first);
		Assert.Empty(first);

		File.WriteAllText(path, "no longer a certificate at all, longer than before");
		// Force a clearly-different last-write-time so the stamp differs even on coarse clocks.
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

		List<string> second = [];
		BackendSettingsValidation.CaPath(path, "ctx", second);
		Assert.Single(second);
		Assert.Contains("CaCertificatePath", second[0]);

		// And back to valid re-validates as valid.
		File.WriteAllText(path, SelfSignedPem());
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10));
		List<string> third = [];
		BackendSettingsValidation.CaPath(path, "ctx", third);
		Assert.Empty(third);
	}
}
