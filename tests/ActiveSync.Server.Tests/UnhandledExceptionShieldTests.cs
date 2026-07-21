using System.Diagnostics;
using System.Text;
using ActiveSync.Server.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E26 — the gateway registers no exception-handling middleware, and
///   <c>WebApplication</c> auto-inserts the developer exception page when
///   <c>ASPNETCORE_ENVIRONMENT=Development</c>. Anything an endpoint lets escape therefore
///   renders a full stack trace to whoever sent the request — including an unauthenticated
///   one. These build the same two-layer pipeline (dev page outermost, shield inside it) and
///   assert the shield swallows the exception so the page never gets to render.
/// </summary>
public sealed class UnhandledExceptionShieldTests
{
	private const string SecretMarker = "SquirrelHarnessSecret";

	[Fact]
	public async Task UnhandledException_DoesNotLeakTheStackTrace_InDevelopment()
	{
		(HttpContext http, string body) = await RunAsync(
			_ => throw new InvalidOperationException(SecretMarker));

		Assert.Equal(StatusCodes.Status500InternalServerError, http.Response.StatusCode);
		Assert.DoesNotContain(SecretMarker, body, StringComparison.Ordinal);
		Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
		Assert.DoesNotContain("at ActiveSync", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnhandledException_IsLogged()
	{
		// Swallowing must not mean losing: the operator still needs the stack trace, just
		// not through the response body.
		RecordingLoggerProvider logs = new();
		await RunAsync(_ => throw new InvalidOperationException(SecretMarker), logs);

		Assert.Contains(logs.Errors, e => e.Contains(SecretMarker, StringComparison.Ordinal));
	}

	[Fact]
	public async Task SuccessfulRequest_IsUntouched()
	{
		(HttpContext http, string body) = await RunAsync(async context =>
		{
			context.Response.StatusCode = StatusCodes.Status200OK;
			await context.Response.WriteAsync("fine");
		});

		Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
		Assert.Equal("fine", body);
	}

	[Fact]
	public async Task ExceptionAfterTheResponseStarted_DoesNotRewriteTheStatus()
	{
		// Once bytes are on the wire the status is already sent; touching it would throw a
		// second exception out of the handler that is supposed to be the last line.
		(HttpContext http, string body) = await RunAsync(async context =>
		{
			context.Response.StatusCode = StatusCodes.Status200OK;
			await context.Response.WriteAsync("partial");
			// DefaultHttpContext's response feature hard-codes HasStarted to false however
			// much you write to it, so the harness flips it the way Kestrel would.
			((TrackingResponseFeature)context.Features.Get<IHttpResponseFeature>()!).HasStarted = true;
			throw new InvalidOperationException(SecretMarker);
		});

		Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
		Assert.Equal("partial", body);
	}

	/// <summary>
	///   Runs one request through [developer exception page] → [shield] → <paramref name="endpoint" />,
	///   the layering <c>WebApplication</c> produces in Development once the shield is the
	///   first thing the app itself registers.
	/// </summary>
	private static async Task<(HttpContext Http, string Body)> RunAsync(
		RequestDelegate endpoint, RecordingLoggerProvider? logs = null)
	{
		ServiceCollection services = new();
		services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
		services.AddMetrics();
		services.AddSingleton(new DiagnosticListener("test"));
		services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
		services.AddSingleton<IOptions<DeveloperExceptionPageOptions>>(
			Options.Create(new DeveloperExceptionPageOptions()));
		services.AddLogging(builder =>
		{
			if (logs is not null)
				builder.AddProvider(logs);
		});
		ServiceProvider provider = services.BuildServiceProvider();

		ApplicationBuilder builder = new(provider);
		builder.UseDeveloperExceptionPage();
		builder.UseUnhandledExceptionShield();
		builder.Run(endpoint);
		RequestDelegate pipeline = builder.Build();

		DefaultHttpContext http = new() { RequestServices = provider };
		http.Request.Path = "/anything";
		MemoryStream body = new();
		http.Features.Set<IHttpResponseFeature>(new TrackingResponseFeature());
		http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

		await pipeline(http);
		return (http, Encoding.UTF8.GetString(body.ToArray()));
	}

	/// <summary>Response feature with a settable <see cref="HasStarted" />, which the default one lacks.</summary>
	private sealed class TrackingResponseFeature : IHttpResponseFeature
	{
		public int StatusCode { get; set; } = StatusCodes.Status200OK;
		public string? ReasonPhrase { get; set; }
		public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
		public Stream Body { get; set; } = Stream.Null;
		public bool HasStarted { get; set; }

		public void OnStarting(Func<object, Task> callback, object state)
		{
		}

		public void OnCompleted(Func<object, Task> callback, object state)
		{
		}
	}

	private sealed class StubEnvironment : IWebHostEnvironment
	{
		public string EnvironmentName { get; set; } = "Development";
		public string ApplicationName { get; set; } = "ActiveSync.Server";
		public string WebRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}

	private sealed class RecordingLoggerProvider : ILoggerProvider
	{
		public List<string> Errors { get; } = [];

		public ILogger CreateLogger(string categoryName)
		{
			return new Recording(this);
		}

		public void Dispose()
		{
		}

		private sealed class Recording(RecordingLoggerProvider owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel, EventId eventId, TState state, Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				if (logLevel < LogLevel.Error)
					return;
				lock (owner.Errors)
					owner.Errors.Add($"{formatter(state, exception)} {exception}");
			}
		}
	}
}
