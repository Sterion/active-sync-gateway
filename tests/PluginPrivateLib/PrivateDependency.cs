namespace PluginPrivateLib;

/// <summary>
///   Reports where the copy of this assembly that the caller actually bound to was loaded from.
///   The fixture plugin surfaces it through <c>DescribeRole</c>, which lets a test tell a
///   plugin-folder load apart from the host's copy without any reflection of its own.
/// </summary>
public static class PrivateDependency
{
	public static string LoadedFrom => typeof(PrivateDependency).Assembly.Location;
}
