using System.Security.Cryptography;
using ActiveSync.Backends.Dav;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Jmap;
using ActiveSync.Backends.Local;
using ActiveSync.Backends.Sieve;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>DI registration helpers that keep Program.cs a thin composition script.</summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	///   The in-repo backend providers, the registry indexing them by name, the parsed
	///   role assignments, and the post-build configuration validator. Used by the server
	///   host and the CLI alike (the CLI validates `eas user` writes with the same rules).
	/// </summary>
	public static IServiceCollection AddBackendProviders(this IServiceCollection services)
	{
		services.AddSingleton<IBackendProvider, ImapBackendProvider>();
		services.AddSingleton<IBackendProvider, JmapBackendProvider>();
		services.AddSingleton<IBackendProvider, SmtpBackendProvider>();
		services.AddSingleton<IBackendProvider, CalDavBackendProvider>();
		services.AddSingleton<IBackendProvider, CardDavBackendProvider>();
		services.AddSingleton<IBackendProvider, SieveBackendProvider>();
		services.AddSingleton<IBackendProvider, LocalBackendProvider>();
		services.AddSingleton<BackendProviderRegistry>();
		services.AddSingleton<BackendConfigurationValidator>();
		// Live-reloadable backend role configuration: the provider rebuilds it when a settings
		// change moves the ActiveSync:Backends subtree (and throws on an invalid STARTUP config,
		// as the old factory did). BackendRolesConfig is exposed as a current snapshot for
		// consumers that read once (the CLI, banners); the resolver and session factory take the
		// provider itself so they see live backend changes.
		services.AddSingleton<BackendRolesProvider>();
		services.AddTransient(sp => sp.GetRequiredService<BackendRolesProvider>().Current);
		return services;
	}

	/// <summary>
	///   Registers the EF Core state store for the configured provider. Each provider has its
	///   own migration set, so we register the matching context subclass but expose it to the
	///   app as the provider-neutral <see cref="SyncDbContext" />. The connection string is read
	///   lazily from <see cref="IOptions{ActiveSyncOptions}" /> (never eagerly from config) so
	///   test hosts and reloads see the final, overridden value.
	/// </summary>
	public static IServiceCollection AddSyncDatabase(this IServiceCollection services, string provider)
	{
		switch (provider.ToLowerInvariant())
		{
			case "postgres":
			case "postgresql":
			case "npgsql":
				AddProvider<NpgsqlSyncDbContext>(services, (sp, db) =>
					db.UseNpgsql(ConnectionString(sp),
						npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory")));
				break;
			case "sqlite":
				AddProvider<SqliteSyncDbContext>(services, (sp, db) =>
					db.UseSqlite(ConnectionString(sp)).AddInterceptors(new SqlitePragmaInterceptor()));
				break;
			default:
				throw new InvalidOperationException(
					$"Unknown database provider '{provider}' (use Sqlite or Postgres).");
		}

		return services;

		static string ConnectionString(IServiceProvider sp)
		{
			string value = sp.GetRequiredService<IOptions<ActiveSyncOptions>>().Value.Database.ConnectionString;
			if (!PostgresConnectionUri.IsPostgresUri(value))
				return value;
			// The validator already rejected unconvertible URIs at startup; this throw only
			// guards hosts that skip options validation.
			return PostgresConnectionUri.TryConvert(value, out string keywordForm, out string? error)
				? keywordForm
				: throw new InvalidOperationException(error);
		}

		static void AddProvider<TContext>(
			IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure)
			where TContext : SyncDbContext
		{
			// The DbContextFactory is registered first — its options must be resolvable from
			// the root scope — and serves the long-lived backend sessions' local stores.
			services.AddDbContextFactory<TContext>(configure);
			// optionsLifetime MUST be Singleton when combined with AddDbContextFactory: the
			// singleton factory resolves the context's options configurations from the root
			// provider, and a scoped registration trips DI scope validation (Development).
			services.AddDbContext<SyncDbContext, TContext>(configure,
				ServiceLifetime.Scoped, ServiceLifetime.Singleton);
			services.AddSingleton<ISyncDbContextFactory>(sp =>
				new SyncDbContextFactoryAdapter<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>()));
		}
	}

	/// <summary>
	///   Registers the local-content encryption singleton. The key is loaded and decoded exactly
	///   once here; configuration errors were already rejected by
	///   <see cref="ActiveSyncOptionsValidator" /> before this factory can run.
	/// </summary>
	public static IServiceCollection AddLocalContentProtection(this IServiceCollection services)
	{
		services.AddSingleton<LocalContentProtector>(sp =>
		{
			EncryptionOptions encryption =
				sp.GetRequiredService<IOptions<ActiveSyncOptions>>().Value.Encryption;
			byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out string? error);
			if (error is not null)
				throw new InvalidOperationException(error);
			if (key is null)
				return LocalContentProtector.CreatePlaintext();
			LocalContentProtector protector = LocalContentProtector.CreateProtected(key);
			CryptographicOperations.ZeroMemory(key);
			return protector;
		});
		return services;
	}

	/// <summary>
	///   Registers one <see cref="IEasCommandHandler" /> per supported EAS command, KEYED by its
	///   command name so <see cref="EasEndpoint" /> resolves exactly the handler a request needs.
	///   The old non-keyed <c>IEnumerable&lt;IEasCommandHandler&gt;</c> injection materialized all
	///   ~19 scoped handlers (and their dependency graphs) per request and discarded 18 — the
	///   largest allocation source on the polling hot path (E4).
	/// </summary>
	public static IServiceCollection AddEasHandlers(this IServiceCollection services)
	{
		services.AddSingleton<MeetingInvitationService>();
		foreach ((string command, Type handler) in EasHandlerRegistrations)
			services.AddKeyedScoped(typeof(IEasCommandHandler), command, handler);
		return services;
	}

	/// <summary>
	///   The command-name → handler-type map used to register (and dispatch) EAS handlers. The
	///   key MUST equal the handler's <see cref="IEasCommandHandler.Command" /> and appear in
	///   <see cref="EasEndpoint" />'s advertised command set — <c>EasHandlerRegistrationTests</c>
	///   locks both correspondences so a typo here surfaces as a test failure, not a 501 in prod.
	/// </summary>
	public static readonly IReadOnlyList<(string Command, Type Handler)> EasHandlerRegistrations =
	[
		("FolderSync", typeof(FolderSyncHandler)),
		("FolderCreate", typeof(FolderCreateHandler)),
		("FolderDelete", typeof(FolderDeleteHandler)),
		("FolderUpdate", typeof(FolderUpdateHandler)),
		("Sync", typeof(SyncHandler)),
		("Ping", typeof(PingHandler)),
		("GetItemEstimate", typeof(GetItemEstimateHandler)),
		("ItemOperations", typeof(ItemOperationsHandler)),
		("GetAttachment", typeof(GetAttachmentHandler)),
		("SendMail", typeof(SendMailHandler)),
		("SmartReply", typeof(SmartReplyHandler)),
		("SmartForward", typeof(SmartForwardHandler)),
		("MoveItems", typeof(MoveItemsHandler)),
		("Search", typeof(SearchHandler)),
		("Find", typeof(FindHandler)),
		("Settings", typeof(SettingsHandler)),
		("MeetingResponse", typeof(MeetingResponseHandler)),
		("ResolveRecipients", typeof(ResolveRecipientsHandler)),
		("Provision", typeof(ProvisionHandler)),
	];
}
