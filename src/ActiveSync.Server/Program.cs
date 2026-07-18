using ActiveSync.Server.Cli;
using Spectre.Console.Cli;

// WebApplicationFactory-based integration tests invoke this entry point WITHOUT a `serve`
// arg (only --key=value settings) and need the web host to build; bare interactive
// invocation shows the banner instead. The test assembly opts its whole process back into
// serving via this env var (module initializer in TestBootstrap).
if (Environment.GetEnvironmentVariable("AS_TEST_FORCE_SERVE") == "1")
	return await Program.RunServerAsync(args);

string[] cliArgs = args switch
{
	// Pre-CLI spelling, kept for existing container HEALTHCHECKs and k8s exec probes.
	["--healthcheck"] => ["healthcheck"],
	["help"] => ["--help"],
	_ => args,
};

// serve and protect accept arbitrary --ActiveSync:Section:Key=value configuration overrides,
// which a strict command parser would reject — dispatch them before Spectre parses. Their
// Spectre commands stay registered so `--help` lists them, delegating to the same methods.
if (cliArgs is ["serve", .. string[] serveArgs])
	return await Program.RunServerAsync(serveArgs);
if (cliArgs is ["protect", .. string[] protectArgs])
	return await CliVerbs.ProtectAsync(protectArgs);

CommandApp<BannerCommand> cli = new();
cli.Configure(CliApp.Configure);
return await cli.RunAsync(cliArgs);

/// <summary>Marker for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
