using System.Security.Cryptography;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Server.Setup;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Command implementations that don't need the web host. Kept as plain methods so both the
///   Spectre commands and Program's pre-parse dispatch (serve/protect pass-through) share them.
/// </summary>
internal static class CliVerbs
{
	/// <summary>
	///   Loads configuration the same way the web host does (appsettings + environment json +
	///   env vars + CLI overrides, then the mounted users file), minus host-only sources.
	/// </summary>
	internal static IConfigurationRoot BuildConfiguration(string[] args)
	{
		string environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
		ConfigurationBuilder builder = new();
		builder.SetBasePath(Directory.GetCurrentDirectory())
			.AddJsonFile("appsettings.json", true)
			.AddJsonFile($"appsettings.{environment}.json", true)
			.AddEnvironmentVariables()
			.AddCommandLine(args);
		IConfigurationRoot config = builder.Build();

		string? usersFile = config["ActiveSync:UsersFile"];
		if (!string.IsNullOrWhiteSpace(usersFile))
		{
			builder.AddJsonFile(Path.GetFullPath(usersFile), false, false);
			config = builder.Build();
		}

		return config;
	}

	/// <summary>
	///   Bare invocation: print the banner for the configuration the gateway WOULD start with
	///   (or the validation errors serve would die with), without starting anything. Database
	///   users are merged in when the state database is reachable.
	/// </summary>
	internal static async Task<int> ShowBannerAsync()
	{
		IConfigurationRoot config = BuildConfiguration([]);
		ActiveSyncOptions? options = config.GetSection("ActiveSync").Get<ActiveSyncOptions>();
		if (options is null)
		{
			await Console.Error.WriteLineAsync(
				"Missing 'ActiveSync' configuration section (appsettings.json or environment variables).");
			return 1;
		}

		ValidateOptionsResult validation = new ActiveSyncOptionsValidator().Validate(null, options);
		if (validation.Failed)
		{
			await Console.Error.WriteLineAsync("The gateway would refuse to start with this configuration:");
			foreach (string failure in validation.Failures ?? [])
				await Console.Error.WriteLineAsync($"  - {failure}");
			return 1;
		}

		IReadOnlyDictionary<string, MergedAccount>? merged = null;
		string? databaseNote = null;
		try
		{
			ServiceCollection services = new();
			services.AddLogging();
			services.AddOptions<ActiveSyncOptions>().Bind(config.GetSection("ActiveSync"));
			services.AddSyncDatabase(PostgresConnectionUri.EffectiveProvider(options.Database));
			services.AddSingleton<AccountStore>();
			await using ServiceProvider provider = services.BuildServiceProvider();
			AccountStore store = provider.GetRequiredService<AccountStore>();
			// Probe first so an unreachable/unmigrated database lands in the catch (the
			// resolver's refresh would swallow it and silently show config-only).
			await store.ReadStampAsync(CancellationToken.None);
			AccountResolver resolver = new(Microsoft.Extensions.Options.Options.Create(options), store);
			await resolver.EnsureFreshAsync(true, CancellationToken.None);
			merged = resolver.MergedUsers;
		}
		catch (Exception ex)
		{
			databaseNote = $"(database users not shown — {ex.Message})";
		}

		// Same console shaping as serve (ActiveSync:Log Mode/Format, operator sinks win),
		// so the bare-eas banner looks exactly like the real startup banner.
		LoggerConfiguration serilogConfiguration = new();
		LoggingSetup.ConfigureConsole(serilogConfiguration, config, true);
		Serilog.Core.Logger serilog = serilogConfiguration.CreateLogger();
		using SerilogLoggerFactory loggerFactory = new(serilog, true);
		ILogger logger = loggerFactory.CreateLogger("ActiveSync.Startup");
		StartupSummary.Log(logger, options, merged);

		Console.WriteLine();
		if (databaseNote is not null)
			Console.WriteLine(databaseNote);
		Console.WriteLine("The gateway is NOT running — this is the configuration it would start with.");
		Console.WriteLine("Start it with 'eas serve'; list all commands with 'eas help'.");
		return 0;
	}

	/// <summary>Container HEALTHCHECK: probe /healthz over the loopback and exit 0/1.</summary>
	internal static async Task<int> HealthcheckAsync()
	{
		string baseUrl = Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url")
		                 ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
		                 ?? "http://localhost:5080";
		baseUrl = baseUrl.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").TrimEnd('/');
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
			using HttpResponseMessage response = await http.GetAsync($"{baseUrl}/healthz");
			return response.IsSuccessStatusCode ? 0 : 1;
		}
		catch
		{
			return 1;
		}
	}

	/// <summary>
	///   Seals a secret from stdin with the encryption master key (enc:v1:...). Secrets go via
	///   stdin so they never enter shell history; extra args are configuration overrides.
	/// </summary>
	internal static async Task<int> ProtectAsync(string[] configArgs)
	{
		IConfigurationRoot config = BuildConfiguration(configArgs);
		EncryptionOptions encryption =
			config.GetSection("ActiveSync:Encryption").Get<EncryptionOptions>() ?? new EncryptionOptions();
		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out string? keyError);
		if (key is null)
		{
			await Console.Error.WriteLineAsync(keyError
				?? "protect requires the ActiveSync:Encryption master key (appsettings.json, " +
				"environment variables, or --ActiveSync:Encryption:Key=...).");
			return 1;
		}

		string secret = (await Console.In.ReadToEndAsync()).TrimEnd('\r', '\n');
		if (secret.Length == 0)
		{
			await Console.Error.WriteLineAsync("Usage: echo -n 'secret' | eas protect");
			return 1;
		}

		await Console.Out.WriteLineAsync(SecretValue.Seal(secret, key));
		CryptographicOperations.ZeroMemory(key);
		return 0;
	}

	/// <summary>Hashes a gateway password from stdin (pbkdf2$...), for per-user overrides.</summary>
	internal static async Task<int> HashPasswordAsync()
	{
		string password = (await Console.In.ReadToEndAsync()).TrimEnd('\r', '\n');
		if (password.Length == 0)
		{
			await Console.Error.WriteLineAsync("Usage: echo -n 'password' | eas hash-password");
			return 1;
		}

		await Console.Out.WriteLineAsync(GatewayPasswordHasher.Hash(password));
		return 0;
	}
}
