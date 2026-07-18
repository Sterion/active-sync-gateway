using System.Runtime.CompilerServices;

namespace ActiveSync.Integration.Tests.Infrastructure;

internal static class TestBootstrap
{
	// Bare no-args invocation of Program shows the CLI banner and exits, but
	// WebApplicationFactory invokes the entry point with empty args and needs the web host.
	// A module initializer runs when the test assembly loads — before any test or fixture —
	// so every factory in the suite (fixture-owned or test-local) gets the serving entry point.
	[ModuleInitializer]
	internal static void ForceServeUnderTestHost()
		=> Environment.SetEnvironmentVariable("AS_TEST_FORCE_SERVE", "1");
}
