namespace ActiveSync.Server.Tests;

/// <summary>E19 — a mistyped ActiveSync:UsersFile mount must fail with an actionable error.</summary>
public sealed class UsersFilePathTests
{
	[Fact]
	public void ResolveUsersFilePath_ReturnsNull_WhenUnset()
	{
		Assert.Null(global::Program.ResolveUsersFilePath(null));
		Assert.Null(global::Program.ResolveUsersFilePath("   "));
	}

	[Fact]
	public void ResolveUsersFilePath_ResolvesRelativePath_WhenFileExists()
	{
		string path = Path.Combine(Path.GetTempPath(), $"eas-users-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, "{}");
		try
		{
			Assert.Equal(Path.GetFullPath(path), global::Program.ResolveUsersFilePath(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	// Before E19 the missing file surfaced as a raw FileNotFoundException from deep inside the
	// configuration builder, with no hint that ActiveSync:UsersFile was the culprit. The guard
	// must instead throw an error naming the setting and the resolved absolute path.
	[Fact]
	public void ResolveUsersFilePath_Throws_NamingSettingAndPath_WhenFileMissing()
	{
		string missing = Path.Combine(Path.GetTempPath(), $"eas-missing-{Guid.NewGuid():N}", "users.json");

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
			() => global::Program.ResolveUsersFilePath(missing));

		Assert.Contains("ActiveSync:UsersFile", ex.Message);
		Assert.Contains(Path.GetFullPath(missing), ex.Message);
	}
}
