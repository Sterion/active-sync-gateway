using System.Text;
using ActiveSync.Core.Backend;

namespace ActiveSync.Server.Eas;

/// <summary>Shared HTTP Basic auth parsing + challenge for the EAS and Autodiscover endpoints.</summary>
public static class HttpBasicAuth
{
	public static BackendCredentials? Parse(string? header)
	{
		if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
			return null;
		try
		{
			string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
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
