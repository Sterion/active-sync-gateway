namespace ActiveSync.Backends.Common;

/// <summary>Shared, redaction-safe fragments for provider <c>DescribeRole</c> banner lines.</summary>
public static class BackendDescription
{
	public static string DescribeCert(bool allowInvalid, string? caPath)
	{
		return allowInvalid ? "any (insecure)" : caPath is null ? "system" : "system+custom CA";
	}
}
