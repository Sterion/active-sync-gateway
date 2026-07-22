using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Server.Setup;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E20 — COVERAGE, not proof. The finding is a theoretical cross-thread memory-ordering gap
///   (the captured-local publish had no barrier); a unit test cannot exhibit the missing barrier.
///   These lock in the holder's publish/read contract and that the backing field is volatile, so
///   the seam the fix introduced can't silently regress to a plain field.
/// </summary>
public sealed class CertificateHolderTests
{
	[Fact]
	public void Current_DefaultsToNull_AndRoundTrips()
	{
		CertificateHolder holder = new();
		Assert.Null(holder.Current);

		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new("CN=eas-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using X509Certificate2 cert = request.CreateSelfSigned(
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

		holder.Current = cert;
		Assert.Same(cert, holder.Current);

		holder.Current = null;
		Assert.Null(holder.Current);
	}

	[Fact]
	public void BackingField_IsVolatile()
	{
		System.Reflection.FieldInfo field = typeof(CertificateHolder)
			.GetField("_current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
		Assert.NotNull(field);
		// A volatile field carries the IsVolatile modreq — the compiler's marker for the barrier.
		Assert.Contains(typeof(System.Runtime.CompilerServices.IsVolatile), field.GetRequiredCustomModifiers());
	}
}
