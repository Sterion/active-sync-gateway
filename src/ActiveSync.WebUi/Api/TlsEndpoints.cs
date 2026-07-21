using ActiveSync.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Read-only view of the gateway's active HTTPS certificate for the admin TLS panel: which
///   mode is serving (self-signed / mounted file / off), plus the certificate's subject, SANs,
///   validity, fingerprint and key — never the private key. The certificate paths themselves are
///   edited through the ordinary Settings editor (the <c>ActiveSync:Tls:*</c> catalogue keys).
/// </summary>
internal static class TlsEndpoints
{
	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("tls", async (
			TlsCertificateResolver resolver, ILoggerFactory loggerFactory, CancellationToken ct) =>
		{
			TlsCertificateInfo info = await resolver.DescribeAsync(
				loggerFactory.CreateLogger("ActiveSync.WebUi.Tls"), ct);
			return Results.Ok(new
			{
				enabled = info.Enabled,
				port = info.Port,
				source = info.Source.ToString(),
				certificatePath = info.CertificatePath,
				subject = info.Subject,
				issuer = info.Issuer,
				subjectAlternativeNames = info.SubjectAlternativeNames,
				notBeforeUtc = info.NotBeforeUtc,
				notAfterUtc = info.NotAfterUtc,
				fingerprint = info.Fingerprint,
				keyAlgorithm = info.KeyAlgorithm,
				keySize = info.KeySize,
				error = info.Error,
			});
		});
	}
}
