using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Backends.Common;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

public class ServerCertificateValidatorTests : IDisposable
{
	private readonly X509Certificate2 _ca;
	private readonly string _caPemPath;
	private readonly X509Certificate2 _leaf;
	private readonly X509Certificate2 _unrelated;
	private readonly X509Certificate2 _intermediate;
	private readonly X509Certificate2 _leafViaIntermediate;

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

		// A private PKI with an ISSUING (intermediate) CA — the documented CaCertificatePath
		// use case is trusting a private ROOT, not every intermediate it ever issues.
		using RSA intermediateKey = RSA.Create(2048);
		CertificateRequest intermediateRequest = new(
			"CN=ActiveSync Test Intermediate CA", intermediateKey, HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		intermediateRequest.CertificateExtensions.Add(
			new X509BasicConstraintsExtension(true, true, 0, true));
		using X509Certificate2 intermediatePublic = intermediateRequest.Create(
			_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20),
			Guid.NewGuid().ToByteArray()[..16]);
		_intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

		using RSA leafViaIntermediateKey = RSA.Create(2048);
		CertificateRequest leafViaIntermediateRequest = new(
			"CN=mail2.test.local", leafViaIntermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		_leafViaIntermediate = leafViaIntermediateRequest.Create(
			_intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10),
			Guid.NewGuid().ToByteArray()[..16]);

		_caPemPath = Path.Combine(Path.GetTempPath(), $"as-test-ca-{Guid.NewGuid():N}.pem");
		File.WriteAllText(_caPemPath, _ca.ExportCertificatePem());
	}

	public void Dispose()
	{
		_ca.Dispose();
		_leaf.Dispose();
		_unrelated.Dispose();
		_intermediate.Dispose();
		_leafViaIntermediate.Dispose();
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

	/// <summary>
	///   Simulates the X509Chain the TLS stack itself builds from the certificates the server
	///   presented during the handshake (leaf + intermediate) — the argument
	///   RemoteCertificateValidationCallback receives as its 3rd parameter.
	/// </summary>
	private X509Chain BuildHandshakeChain()
	{
		X509Chain chain = new();
		chain.ChainPolicy.ExtraStore.Add(_intermediate);
		chain.ChainPolicy.ExtraStore.Add(_ca);
		chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
		chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
		chain.Build(_leafViaIntermediate);
		return chain;
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

	// Coverage — checkRevocation is a new knob this fix introduces; unmodified code has no
	// such parameter to exercise, so there is no black-box red state to observe without first
	// changing the signature. RevocationMode was hardcoded to NoCheck unconditionally, so a
	// revoked backend certificate on a real private PKI was silently accepted with no opt-out.
	// This proves the knob is actually wired into the chain build, not merely accepted and
	// ignored: the test CA/leaf carry no CRL/OCSP endpoint (as most lab/private CAs don't), so
	// enabling revocation checking makes the chain's revocation status unknowable — and .NET's
	// chain engine fails the build closed on that, without any live CRL/OCSP server involved.
	[Fact]
	public void CustomCa_WithRevocationCheckEnabled_RejectsChainWithNoRevocationInfo()
	{
		Assert.False(ServerCertificateValidator.Validate(
			_leaf, SslPolicyErrors.RemoteCertificateChainErrors, false, CustomCas(), checkRevocation: true));
	}

	// The default (omitted / false) must keep today's behaviour unchanged for the exact same chain.
	[Fact]
	public void CustomCa_WithRevocationCheckOmitted_StillAcceptsLeafSignedByIt()
	{
		Assert.True(ServerCertificateValidator.Validate(
			_leaf, SslPolicyErrors.RemoteCertificateChainErrors, false, CustomCas()));
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

	// CreateCallback's returned delegate discarded its own 3rd parameter (the X509Chain the
	// TLS stack built from the handshake's own certificates), then built a FRESH chain with only
	// the custom-CA PEM and no ExtraStore. A leaf signed by a private INTERMEDIATE (not directly
	// by the trusted root — CaCertificatePath's documented use case is trusting a private ROOT)
	// therefore failed validation unless the intermediate happened to be cached locally or
	// fetchable via AIA. Invoking the callback delegate directly — its signature is fixed by
	// RemoteCertificateValidationCallback regardless of the fix — proves this red-first without
	// needing any new parameter on the public surface.
	[Fact]
	public void CreateCallback_UsesTheHandshakeChain_ToAcceptALeafSignedThroughAnIntermediate()
	{
		RemoteCertificateValidationCallback? callback =
			ServerCertificateValidator.CreateCallback(false, _caPemPath);
		Assert.NotNull(callback);
		using X509Chain handshakeChain = BuildHandshakeChain();

		bool result = callback!(
			this, _leafViaIntermediate, handshakeChain, SslPolicyErrors.RemoteCertificateChainErrors);

		Assert.True(result);
	}

	// LoadCaCertificates keyed its cache on the path alone with no invalidation, so an
	// operator rotating a private CA at the same CaCertificatePath (a live, DB-settable option
	// that otherwise applies on session recycle per AGENTS.md) kept seeing the OLD CA forever --
	// including past the point the old root expires, with no configuration change able to fix it.
	[Fact]
	public void LoadCaCertificates_PicksUpARotatedFileAtTheSamePath()
	{
		string path = Path.Combine(Path.GetTempPath(), $"as-test-rotate-{Guid.NewGuid():N}.pem");
		using RSA firstKey = RSA.Create(2048);
		CertificateRequest firstRequest = new(
			"CN=Rotate Test CA First", firstKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using X509Certificate2 first = firstRequest.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
		File.WriteAllText(path, first.ExportCertificatePem());

		try
		{
			X509Certificate2Collection loadedFirst = ServerCertificateValidator.LoadCaCertificates(path);
			Assert.Equal(first.Thumbprint, Assert.Single(loadedFirst.Cast<X509Certificate2>()).Thumbprint);

			// Rotate: a brand-new CA written to the SAME path (an operator re-issuing their
			// private root and rewriting the PEM file in place).
			using RSA secondKey = RSA.Create(2048);
			CertificateRequest secondRequest = new(
				"CN=Rotate Test CA Second", secondKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			using X509Certificate2 second = secondRequest.CreateSelfSigned(
				DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
			File.WriteAllText(path, second.ExportCertificatePem());

			X509Certificate2Collection loadedSecond = ServerCertificateValidator.LoadCaCertificates(path);
			Assert.Equal(second.Thumbprint, Assert.Single(loadedSecond.Cast<X509Certificate2>()).Thumbprint);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void CreateCallback_WithoutAnyHandshakeCertificates_StillRejectsTheIntermediateLeaf()
	{
		// Coverage: pins that a callback receiving no chain information at all (an empty chain,
		// as some callers might legitimately pass) still fails closed — the fix's benefit comes
		// specifically from the handshake chain's own ChainElements, not from some other change.
		RemoteCertificateValidationCallback? callback =
			ServerCertificateValidator.CreateCallback(false, _caPemPath);
		Assert.NotNull(callback);
		using X509Chain emptyChain = new();

		bool result = callback!(
			this, _leafViaIntermediate, emptyChain, SslPolicyErrors.RemoteCertificateChainErrors);

		Assert.False(result);
	}
}
