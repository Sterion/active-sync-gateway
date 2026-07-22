using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ActiveSync.Backends.Dav;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H1: the DAV readiness probe used to hardcode
///   <c>RemoteCertificateValidationCallback =&gt; true</c>, so an https endpoint presenting a
///   certificate the operator never opted into was still reported reachable — overriding the very
///   TLS settings the operator configured. The probe must instead honor
///   <c>AllowInvalidCertificates</c> / <c>CaCertificatePath</c> exactly as a real request would.
/// </summary>
public sealed class DavReadinessTests
{
	// A self-signed cert presented by a throwaway TLS server is untrusted (unknown root, and its
	// CN does not match 127.0.0.1). With allowInvalidCertificates:false the handshake must fail —
	// the probe throws rather than falsely reporting the server reachable.
	[Fact]
	public async Task Probe_UntrustedCertificate_IsRejected_WhenInvalidCertsNotAllowed()
	{
		using SelfSignedTlsServer server = SelfSignedTlsServer.Start();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		await Assert.ThrowsAnyAsync<Exception>(() =>
			DavReadiness.ProbeAsync(server.Url, allowInvalidCertificates: false, caCertificatePath: null, cts.Token));
	}

	// The same endpoint, with allowInvalidCertificates:true, is the lab opt-in — the probe accepts
	// the certificate and reports reachable.
	[Fact]
	public async Task Probe_UntrustedCertificate_IsAccepted_WhenInvalidCertsAllowed()
	{
		using SelfSignedTlsServer server = SelfSignedTlsServer.Start();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		bool ready = await DavReadiness.ProbeAsync(
			server.Url, allowInvalidCertificates: true, caCertificatePath: null, cts.Token);

		Assert.True(ready);
	}

	/// <summary>A minimal TLS server on 127.0.0.1 that answers one request with HTTP 200.</summary>
	private sealed class SelfSignedTlsServer : IDisposable
	{
		private readonly TcpListener _listener;
		private readonly X509Certificate2 _cert;
		private readonly CancellationTokenSource _cts = new();

		private SelfSignedTlsServer(TcpListener listener, X509Certificate2 cert)
		{
			_listener = listener;
			_cert = cert;
		}

		public string Url => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

		public static SelfSignedTlsServer Start()
		{
			using RSA rsa = RSA.Create(2048);
			CertificateRequest req = new("CN=dav-readiness-test", rsa, HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);
			X509Certificate2 cert = req.CreateSelfSigned(
				DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
			// On Windows the private key must be persisted through a PFX round-trip to be usable
			// for server auth; do it unconditionally — harmless elsewhere.
			X509Certificate2 usable = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
			cert.Dispose();

			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			SelfSignedTlsServer server = new(listener, usable);
			_ = server.AcceptLoopAsync();
			return server;
		}

		private async Task AcceptLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(_cts.Token);
				}
				catch
				{
					return;
				}

				_ = HandleAsync(client);
			}
		}

		private async Task HandleAsync(TcpClient client)
		{
			using (client)
			{
				try
				{
					await using SslStream tls = new(client.GetStream());
					await tls.AuthenticateAsServerAsync(_cert);
					// The handshake is what the probe validates; a tiny OPTIONS request needs no
					// draining before the response is written back. Connection: close asks the client
					// to hang up as soon as it has the response.
					byte[] response = Encoding.ASCII.GetBytes(
						"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
					await tls.WriteAsync(response);
					await tls.FlushAsync();
					// Wait for the client to close its side before we dispose the socket. Closing
					// immediately after the flush RSTs the loopback connection on Windows, and the
					// probe's read faults with a socket error before it sees the 200. Reading until
					// EOF (bounded) lets the response actually reach the client first.
					try
					{
						using CancellationTokenSource drain = new(TimeSpan.FromSeconds(5));
						byte[] sink = new byte[256];
						while (await tls.ReadAsync(sink, drain.Token) > 0)
						{
						}
					}
					catch
					{
						// Client closed, or the drain timed out — either way it has the response.
					}
				}
				catch
				{
					// A rejected handshake (allowInvalidCertificates:false) faults here — expected.
				}
			}
		}

		public void Dispose()
		{
			_cts.Cancel();
			_listener.Stop();
			_cert.Dispose();
			_cts.Dispose();
		}
	}
}
