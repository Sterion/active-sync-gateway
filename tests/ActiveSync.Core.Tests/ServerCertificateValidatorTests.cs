using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Backends;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

public class ServerCertificateValidatorTests : IDisposable
{
	private readonly X509Certificate2 _ca;
	private readonly string _caPemPath;
	private readonly X509Certificate2 _leaf;
	private readonly X509Certificate2 _unrelated;

	public ServerCertificateValidatorTests()
	{
		using RSA caKey = RSA.Create(2048);
		CertificateRequest caRequest = new(
			"CN=ActiveSync Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		caRequest.CertificateExtensions.Add(
			new X509BasicConstraintsExtension(true, false, 0, true));
		_ca = caRequest.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

		using RSA leafKey = RSA.Create(2048);
		CertificateRequest leafRequest = new(
			"CN=mail.test.local", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		_leaf = leafRequest.Create(
			_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10),
			Guid.NewGuid().ToByteArray()[..16]);

		using RSA otherKey = RSA.Create(2048);
		CertificateRequest otherRequest = new(
			"CN=imposter.test.local", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		_unrelated = otherRequest.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

		_caPemPath = Path.Combine(Path.GetTempPath(), $"as-test-ca-{Guid.NewGuid():N}.pem");
		File.WriteAllText(_caPemPath, _ca.ExportCertificatePem());
	}

	public void Dispose()
	{
		_ca.Dispose();
		_leaf.Dispose();
		_unrelated.Dispose();
		try
		{
			File.Delete(_caPemPath);
		}
		catch (IOException)
		{
			// temp file cleanup is best effort
		}

		GC.SuppressFinalize(this);
	}

	private X509Certificate2Collection CustomCas()
	{
		return ServerCertificateValidator.LoadCaCertificates(_caPemPath);
	}

	[Fact]
	public void AllowInvalid_AcceptsBrokenChain()
	{
		Assert.True(ServerCertificateValidator.Validate(
			_unrelated, SslPolicyErrors.RemoteCertificateChainErrors, true, null));
	}

	[Fact]
	public void NoErrors_IsAcceptedWithoutKnobs()
	{
		Assert.True(ServerCertificateValidator.Validate(
			_leaf, SslPolicyErrors.None, false, null));
	}

	[Fact]
	public void CustomCa_AcceptsLeafSignedByIt()
	{
		Assert.True(ServerCertificateValidator.Validate(
			_leaf, SslPolicyErrors.RemoteCertificateChainErrors, false, CustomCas()));
	}

	[Fact]
	public void CustomCa_RejectsUnrelatedCertificate()
	{
		Assert.False(ServerCertificateValidator.Validate(
			_unrelated, SslPolicyErrors.RemoteCertificateChainErrors, false, CustomCas()));
	}

	[Fact]
	public void CustomCa_NeverRepairsNameMismatch()
	{
		Assert.False(ServerCertificateValidator.Validate(
			_leaf,
			SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
			false, CustomCas()));
	}

	[Fact]
	public void ChainErrors_WithoutKnobs_AreRejected()
	{
		Assert.False(ServerCertificateValidator.Validate(
			_leaf, SslPolicyErrors.RemoteCertificateChainErrors, false, null));
	}

	[Fact]
	public void CreateCallback_ReturnsNull_WhenNoKnobsSet()
	{
		Assert.Null(ServerCertificateValidator.CreateCallback(false, null));
	}

	[Fact]
	public void SettingsValidation_RejectsMissingCaFile()
	{
		List<string> failures = new();
		BackendSettingsValidation.CaPath(
			Path.Combine(Path.GetTempPath(), "does-not-exist.pem"), "imap (MailStore)", failures);
		Assert.Contains("does not exist", string.Join(";", failures));
	}

	[Fact]
	public void SettingsValidation_RejectsGarbageCaFile()
	{
		string garbage = Path.Combine(Path.GetTempPath(), $"as-test-garbage-{Guid.NewGuid():N}.pem");
		File.WriteAllText(garbage, "this is not a certificate");
		try
		{
			List<string> failures = new();
			BackendSettingsValidation.CaPath(garbage, "smtp (MailSubmit)", failures);
			Assert.NotEmpty(failures);
		}
		finally
		{
			File.Delete(garbage);
		}
	}

	[Fact]
	public void SettingsValidation_AcceptsRealCaFile()
	{
		List<string> failures = new();
		BackendSettingsValidation.CaPath(_caPemPath, "imap (MailStore)", failures);
		Assert.Empty(failures);
	}
}
