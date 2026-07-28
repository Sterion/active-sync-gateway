using ActiveSync.Server.Cli;

namespace ActiveSync.Server.Tests;

/// <summary>A mistyped ActiveSync:UsersFile mount must fail with an actionable error.</summary>
[Collection("cli")]
public sealed class UsersFilePathTests : IDisposable
{
	private readonly Dictionary<string, string?> _originalEnv = [];

	public void Dispose()
	{
		foreach ((string name, string? value) in _originalEnv)
			Environment.SetEnvironmentVariable(name, value);
	}

	private void SetEnv(string name, string? value)
	{
		_originalEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
		Environment.SetEnvironmentVariable(name, value);
	}

	// CliVerbs.BuildConfiguration is reached from every non-serve verb — including inside the
	// warm gateway via /cli (ShowBannerAsync, ProtectAsync, CliServices' two providers) — and used
	// Path.GetFullPath + a REQUIRED AddJsonFile directly, bypassing the Program.ResolveUsersFilePath
	// guard added specifically so a typo'd mount surfaces an actionable error instead of a raw
	// FileNotFoundException from deep inside the configuration builder.
	[Fact]
	public void BuildConfiguration_MissingUsersFile_ThrowsActionableError_NotRawFileNotFoundException()
	{
		string missing = Path.Combine(Path.GetTempPath(), $"eas-missing-{Guid.NewGuid():N}", "users.json");
		SetEnv("ActiveSync__UsersFile", missing);

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
			() => CliVerbs.BuildConfiguration([]));

		Assert.Contains("ActiveSync:UsersFile", ex.Message);
		Assert.Contains(Path.GetFullPath(missing), ex.Message);
	}

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

	// Before this guard, the missing file surfaced as a raw FileNotFoundException from deep inside the
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
