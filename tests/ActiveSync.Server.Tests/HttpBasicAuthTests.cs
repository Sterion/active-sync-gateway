using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Tests;

public sealed class HttpBasicAuthTests
{
	private static string Encode(string user, string password)
	{
		return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
	}

	[Fact]
	public void OversizedHeader_IsRejectedBeforeDecoding()
	{
		// E21: the header is unauthenticated input and was base64-decoded and UTF-8 materialized
		// whole, so one request could make the gateway allocate a header-sized string per attempt.
		string header = Encode("user@example.com", new string('p', 512 * 1024));

		Assert.Null(HttpBasicAuth.Parse(header));
	}

	[Fact]
	public void NormalCredentials_StillParse()
	{
		BackendCredentials? credentials = HttpBasicAuth.Parse(Encode("user@example.com", "s3cret:with:colons"));

		Assert.NotNull(credentials);
		Assert.Equal("user@example.com", credentials.UserName);
		Assert.Equal("s3cret:with:colons", credentials.Password);
	}

	[Fact]
	public void DomainQualifiedUser_LosesTheDomain()
	{
		BackendCredentials? credentials = HttpBasicAuth.Parse(Encode(@"CORP\user", "pw"));

		Assert.NotNull(credentials);
		Assert.Equal("user", credentials.UserName);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Bearer abc")]
	[InlineData("Basic ")]
	[InlineData("Basic !!!not-base64!!!")]
	public void MalformedHeader_IsRejected(string? header)
	{
		Assert.Null(HttpBasicAuth.Parse(header));
	}
}
