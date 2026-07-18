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
	public void OptionsValidator_RejectsMissingCaFile()
	{
		ActiveSyncOptions options = ValidOptions();
		options.Imap.CaCertificatePath = Path.Combine(Path.GetTempPath(), "does-not-exist.pem");
		ValidateOptionsResult result = new ActiveSyncOptionsValidator().Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("does not exist", result.FailureMessage);
	}

	[Fact]
	public void OptionsValidator_RejectsGarbageCaFile()
	{
		string garbage = Path.Combine(Path.GetTempPath(), $"as-test-garbage-{Guid.NewGuid():N}.pem");
		File.WriteAllText(garbage, "this is not a certificate");
		try
		{
			ActiveSyncOptions options = ValidOptions();
			options.Smtp.CaCertificatePath = garbage;
			ValidateOptionsResult result = new ActiveSyncOptionsValidator().Validate(null, options);
			Assert.True(result.Failed);
		}
		finally
		{
			File.Delete(garbage);
		}
	}

	[Fact]
	public void OptionsValidator_AcceptsRealCaFile()
	{
		ActiveSyncOptions options = ValidOptions();
		options.Imap.CaCertificatePath = _caPemPath;
		ValidateOptionsResult result = new ActiveSyncOptionsValidator().Validate(null, options);
		Assert.True(result.Succeeded);
	}

	private static ActiveSyncOptions ValidOptions()
	{
		return new ActiveSyncOptions
		{
			Imap = new ImapOptions { Host = "imap.example.com" },
			Smtp = new SmtpOptions { Host = "smtp.example.com" },
			Encryption = new EncryptionOptions { AllowPlaintext = true }
		};
	}
}
