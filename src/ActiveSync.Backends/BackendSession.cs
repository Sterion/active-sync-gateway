using ActiveSync.Backends.Dav;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Backends.Sieve;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends;

/// <summary>
///   Composite backend session: IMAP/SMTP mail plus CalDAV/CardDAV stores when configured;
///   content classes without an external backend are served from the gateway database
///   (local stores). Notes are always local — no DAV backend carries them.
/// </summary>
public sealed class BackendSession : IBackendSession
{
	private readonly WebDavClient? _calDavClient;
	private readonly WebDavClient? _cardDavClient;
	private readonly ImapSession _imapSession;
	private readonly List<IContentStore> _stores = [];

	private readonly CalDavStore? _calDavStore;

	public BackendSession(
		ResolvedAccount account,
		BackendCredentials gatewayCredentials,
		ISyncDbContextFactory dbFactory,
		LocalChangeNotifier notifier,
		LocalContentProtector protector,
		Func<string, ImapIdleWatcher?> idleWatcherProvider,
		ILogger logger,
		ILoggerFactory loggerFactory,
		IReadOnlyList<SharedCollection>? sharedCalendars = null)
	{
		// The gateway credentials are the IDENTITY (DB scoping, encryption AAD, cache keys);
		// each backend authenticates with the account's resolved per-backend credentials.
		// In PassThrough mode the two are the same object.
		Credentials = gatewayCredentials;
		MailAddress = account.MailAddress;
		string partStatIdentity = account.MailAddress ?? account.GatewayLogin;

		// Verbose wire logging: per-backend categories so one protocol can be traced alone
		// (e.g. Serilog override ActiveSync.Backends.Smtp=Verbose).
		ILogger imapWireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Imap");
		ILogger smtpWireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Smtp");
		ILogger davWireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Dav");

		_imapSession = new ImapSession(account.Imap.Options, account.Imap.Credentials, logger, imapWireLogger);
		ImapMailBackend mailBackend = new(
			_imapSession, account.Smtp.Options, account.Smtp.Credentials, account.MailAddress,
			idleWatcherProvider, logger, smtpWireLogger);
		Mail = mailBackend;
		_stores.Add(mailBackend);

		if (account.CalDav is not null)
		{
			DavServerOptions calDavOptions = account.CalDav.Options;
			_calDavClient = new WebDavClient(new Uri(calDavOptions.BaseUrl), account.CalDav.Credentials,
				calDavOptions.AllowInvalidCertificates, calDavOptions.CaCertificatePath, davWireLogger);
			CalDavStore calStore = new(_calDavClient, calDavOptions, account.CalDav.Credentials,
				partStatIdentity, logger, sharedCalendars);
			Calendar = calStore;
			_calDavStore = calStore;
			_stores.Add(calStore);
		}
		else
		{
			LocalCalendarStore localCalendar = new(
				dbFactory, notifier, gatewayCredentials, protector, partStatIdentity);
			Calendar = localCalendar;
			_stores.Add(localCalendar);
		}

		// Tasks: CalDAV VTODO collection when configured (Axigen ships a "Tasks"
		// collection), otherwise the gateway database.
		if (account.CalDav is not null && !string.IsNullOrWhiteSpace(account.CalDav.Options.TaskFolder))
			_stores.Add(new CalDavTaskStore(
				_calDavClient!, account.CalDav.Options, account.CalDav.Credentials, logger));
		else
			_stores.Add(new LocalTaskStore(dbFactory, notifier, gatewayCredentials, protector));

		if (account.CardDav is not null)
		{
			DavServerOptions cardDavOptions = account.CardDav.Options;
			_cardDavClient = new WebDavClient(new Uri(cardDavOptions.BaseUrl), account.CardDav.Credentials,
				cardDavOptions.AllowInvalidCertificates, cardDavOptions.CaCertificatePath, davWireLogger);
			CardDavStore cardStore = new(_cardDavClient, cardDavOptions, account.CardDav.Credentials, logger);
			Contacts = cardStore;
			_stores.Add(cardStore);
		}
		else
		{
			LocalContactStore localContacts = new(dbFactory, notifier, gatewayCredentials, protector);
			Contacts = localContacts;
			_stores.Add(localContacts);
		}

		_stores.Add(new LocalNotesStore(dbFactory, notifier, gatewayCredentials, protector));

		if (account.Sieve is not null)
			Oof = new SieveOofBackend(account.Sieve.Options, account.Sieve.Credentials,
				loggerFactory.CreateLogger("ActiveSync.Backends.Sieve"));
	}

	internal DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

	public BackendCredentials Credentials { get; }
	public string? MailAddress { get; }
	public IReadOnlyList<IContentStore> Stores => _stores;
	public IMailOperations Mail { get; }
	public IContactOperations? Contacts { get; }
	public ICalendarOperations? Calendar { get; }
	public IOofBackend? Oof { get; }

	public IContentStore? GetStoreForClass(string easClass)
	{
		return _stores.FirstOrDefault(s => s.EasClass.Equals(easClass, StringComparison.OrdinalIgnoreCase));
	}

	public IContentStore? GetStoreForBackendKey(string backendKey)
	{
		if (backendKey.StartsWith(ImapSession.KeyPrefix, StringComparison.Ordinal))
			return GetStoreForClass(EasClass.Email);
		if (backendKey.StartsWith(CalDavTaskStore.KeyPrefix, StringComparison.Ordinal))
			return GetStoreForClass(EasClass.Tasks);
		if (backendKey.StartsWith(CalDavStore.KeyPrefix, StringComparison.Ordinal))
			return GetStoreForClass(EasClass.Calendar);
		if (backendKey.StartsWith(CardDavStore.KeyPrefix, StringComparison.Ordinal))
			return GetStoreForClass(EasClass.Contacts);
		if (backendKey.StartsWith(LocalStoreBase.KeyPrefix, StringComparison.Ordinal))
			return _stores.FirstOrDefault(s => s is LocalStoreBase local && local.FolderBackendKey == backendKey);
		return null;
	}

	public bool IsReadOnlyFolder(string folderBackendKey)
	{
		return _calDavStore is not null &&
		       folderBackendKey.StartsWith(CalDavStore.KeyPrefix, StringComparison.Ordinal) &&
		       _calDavStore.IsReadOnlyCollection(folderBackendKey);
	}

	public async ValueTask DisposeAsync()
	{
		await _imapSession.DisposeAsync().ConfigureAwait(false);
		_calDavClient?.Dispose();
		_cardDavClient?.Dispose();
	}
}
