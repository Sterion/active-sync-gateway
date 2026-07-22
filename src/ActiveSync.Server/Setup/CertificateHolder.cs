using System.Security.Cryptography.X509Certificates;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Holds the serving TLS certificate behind a memory barrier (E20). The startup path writes it
///   once the certificate has loaded; Kestrel reads it on connection threads through the HTTPS
///   selector. The <c>volatile</c> field supplies the happens-before ordering the previous
///   captured-local pattern relied on the intervening <c>await</c>s to provide — ordering that
///   would be lost silently under refactoring (moving the load below <c>RunAsync</c>) or a second
///   write for certificate rotation. It is also the natural seam for future hot rotation.
/// </summary>
public sealed class CertificateHolder
{
	private volatile X509Certificate2? _current;

	public X509Certificate2? Current
	{
		get => _current;
		set => _current = value;
	}
}
