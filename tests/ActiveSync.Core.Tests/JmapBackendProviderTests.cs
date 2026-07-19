using ActiveSync.Backends.Jmap;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>The jmap provider's config surface: supported roles, validation and the banner line.</summary>
public class JmapBackendProviderTests
{
	private static JmapBackendProvider Provider()
	{
		return new JmapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance);
	}

	private static ProviderSettings Settings(params (string Key, string? Value)[] values)
	{
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(values.ToDictionary(v => $"jmap:{v.Key}", v => v.Value))
			.Build();
		return new ProviderSettings(config.GetSection("jmap"));
	}

	[Fact]
	public void SupportsOnlyMailRoles_InStage1()
	{
		JmapBackendProvider provider = Provider();
		Assert.Equal("jmap", provider.Name);
		Assert.Contains(BackendRole.MailStore, provider.SupportedRoles);
		Assert.Contains(BackendRole.MailSubmit, provider.SupportedRoles);
		Assert.DoesNotContain(BackendRole.Notes, provider.SupportedRoles);
		Assert.DoesNotContain(BackendRole.Tasks, provider.SupportedRoles);
	}

	[Fact]
	public void ValidateConfiguration_AcceptsAbsoluteHttpUrl()
	{
		List<string> failures = new();
		Provider().ValidateConfiguration(
			BackendRole.MailStore, Settings(("BaseUrl", "https://mail.example.com")), failures);
		Assert.Empty(failures);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-url")]
	[InlineData("ftp://mail.example.com")]
	public void ValidateConfiguration_RejectsNonHttpBaseUrl(string baseUrl)
	{
		List<string> failures = new();
		Provider().ValidateConfiguration(BackendRole.MailStore, Settings(("BaseUrl", baseUrl)), failures);
		Assert.Contains(failures, f => f.Contains("BaseUrl"));
	}

	[Fact]
	public void DescribeRole_IsRedactedAndNamesTheEndpoint()
	{
		string line = Provider().DescribeRole(
			BackendRole.MailStore, Settings(("BaseUrl", "https://mail.example.com")));
		Assert.Contains("jmap", line);
		Assert.Contains("https://mail.example.com", line);
		Assert.Contains("cert=", line);
	}
}
