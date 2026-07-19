namespace ActiveSync.WebUi.Auth;

/// <summary>Shared auth constants of the web interfaces.</summary>
internal static class WebUiAuth
{
	/// <summary>The cookie authentication scheme name (one identity for both portals).</summary>
	internal const string Scheme = "WebUi";

	internal const string CookieName = "eas.webui";

	/// <summary>Claim carrying the admin capability ("true" grants /admin).</summary>
	internal const string AdminClaim = "eas:admin";

	/// <summary>Policy: authenticated AND admin — everything under /admin/api.</summary>
	internal const string AdminPolicy = "WebUiAdmin";

	/// <summary>Policy: any authenticated web session — everything under /user/api.</summary>
	internal const string UserPolicy = "WebUiUser";

	/// <summary>
	///   CSRF companion header required on every non-GET API call: a cross-site request can
	///   neither send the SameSite=Strict cookie nor set a custom header, so the pair replaces
	///   the whole antiforgery-token machinery for this JSON-only API.
	/// </summary>
	internal const string CsrfHeader = "X-EAS-WebUi";
}
