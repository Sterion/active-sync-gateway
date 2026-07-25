using System.Text;
using ActiveSync.Server.Eas;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E11: <see cref="AutodiscoverEndpoint.ExtractEmailAsync" /> used to buffer the whole POST body
///   into one string with <c>ReadToEndAsync</c> before parsing it — an authenticated caller could
///   send up to <c>MaxRequestBodySize</c> (64 MB) and have it fully buffered and XML-parsed on every
///   request. Real Autodiscover bodies are a few hundred bytes, so the read is now capped well below
///   that — an oversized body is treated like any other malformed request (no email extracted, the
///   caller falls back to the login) rather than fully consumed.
/// </summary>
public sealed class AutodiscoverBodyCapTests
{
	private const string RequestNs =
		"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/requestschema/2006";

	private static DefaultHttpContext PostRequest(string body)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(body);
		DefaultHttpContext http = new();
		http.Request.Method = "POST";
		http.Request.Body = new MemoryStream(bytes);
		http.Request.ContentLength = bytes.Length;
		return http;
	}

	[Fact]
	public async Task ExtractEmailAsync_ParsesASmallWellFormedRequest()
	{
		string body = $"""
			<Autodiscover xmlns="{RequestNs}">
			  <Request>
			    <EMailAddress>alice@example.com</EMailAddress>
			  </Request>
			</Autodiscover>
			""";

		string? email = await AutodiscoverEndpoint.ExtractEmailAsync(
			PostRequest(body), NullLogger.Instance, CancellationToken.None);

		Assert.Equal("alice@example.com", email);
	}

	[Fact]
	public async Task ExtractEmailAsync_OversizedBody_IsNotFullyBufferedOrParsed()
	{
		// Well past a "few KB" cap but small enough to keep the test fast — still perfectly valid,
		// parseable XML carrying a real EMailAddress, so the ONLY reason extraction can fail is size.
		string padding = new string('x', 32 * 1024);
		string body = $"""
			<Autodiscover xmlns="{RequestNs}">
			  <Request>
			    <EMailAddress>huge@example.com</EMailAddress>
			    <Padding>{padding}</Padding>
			  </Request>
			</Autodiscover>
			""";

		string? email = await AutodiscoverEndpoint.ExtractEmailAsync(
			PostRequest(body), NullLogger.Instance, CancellationToken.None);

		Assert.Null(email);
	}
}
