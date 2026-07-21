using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Server.Eas;

/// <summary>Shared HTTP Basic auth parsing + challenge for the EAS and Autodiscover endpoints.</summary>
public static class HttpBasicAuth
{
	/// <summary>
	///   Upper bound on the base64 blob in an Authorization header. RFC 7617 credentials are just
	///   <c>user:password</c>; 2048 characters leaves room for ~1.5 KB of them. The bound matters
	///   because the header is unauthenticated input on the phone-facing listener and the decode
	///   allocates twice (bytes, then the UTF-8 string) before anything has been verified — with
	///   no bound, every rejected request costs the gateway a header-sized allocation.
	/// </summary>
	public const int MaxCredentialChars = 2048;

	public static BackendCredentials? Parse(string? header)
	{
		if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
			return null;
		try
		{
			string encoded = header[6..].Trim();
			if (encoded.Length is 0 or > MaxCredentialChars)
				return null;
			string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
			int colon = decoded.IndexOf(':');
			if (colon <= 0)
				return null;
			string user = decoded[..colon];
			string password = decoded[(colon + 1)..];
			// Some clients send DOMAIN\user — strip the domain.
			int backslash = user.LastIndexOf('\\');
			if (backslash >= 0)
				user = user[(backslash + 1)..];
			return new BackendCredentials(user, password);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	public static void Challenge(HttpContext http)
	{
		http.Response.StatusCode = StatusCodes.Status401Unauthorized;
		http.Response.Headers.WWWAuthenticate = "Basic realm=\"ActiveSync\"";
	}
}
