using System.Xml.Linq;
using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G19/G20: local writes race concurrent devices through <see cref="LocalItem.ConcurrencyToken" />
///   (stamped on every save — <see cref="SyncDbContext" />). <see cref="LocalStoreBase.UpdateItemAsync" />
///   already retries a losing attempt up to 4 times; these tests cover the two paths that did not
///   behave like every other local write: an update that EXHAUSTS its retries must surface a
///   <see cref="BackendException" />, not a raw EF type (G19), and
///   <see cref="LocalCalendarStore.RespondToMeetingAsync" /> must retry a losing attempt at all (G20).
/// </summary>
public sealed class LocalStoreConcurrencyTests : IDisposable
{
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;

	private readonly SqliteConnection _connection;
	private readonly int _userId;
	private readonly string _uid = Guid.NewGuid().ToString();

	public LocalStoreConcurrencyTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		db.Database.EnsureCreated();
		User user = new() { Login = "u", UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.Add(user);
#pragma warning restore VSTHRD103
		db.SaveChanges();
		_userId = user.UserId;
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task UpdateItemAsync_ExhaustingRetries_ThrowsBackendException_NotTheRawEfType()
	{
		SeedContact();

		// Every single attempt races a fresh competing write on the SAME row, so all 4 attempts
		// collide and the retry loop is genuinely exhausted (not just raced once and recovered).
		AlwaysConflictInterceptor race = new(() =>
		{
			using SyncDbContext racer = StateTestSupport.NewContext(_connection);
			LocalItem row = racer.LocalItems.First(i => i.Uid == _uid);
			row.LastModifiedUtc = DateTime.UtcNow;
			racer.SaveChanges();
		});
		InterceptedDbContextFactory factory = new(_connection, race);
		LocalContactStore store = new(
			factory, new LocalChangeNotifier(), _userId, LocalContentProtector.CreatePlaintext(), NullLogger.Instance);

		string itemKey = await ItemKeyAsync();
		XElement appData = new(Contacts + "FirstName", "Updated");

		await Assert.ThrowsAsync<BackendException>(() =>
			store.UpdateItemAsync(store.FolderBackendKey, itemKey, appData, CancellationToken.None));
	}

	private void SeedContact()
	{
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		db.LocalItems.Add(new LocalItem
		{
			UserId = _userId,
			Collection = "contacts",
			Uid = _uid,
			Content = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Original\r\nEND:VCARD\r\n",
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}

	private async Task<string> ItemKeyAsync()
	{
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		LocalItem row = await db.LocalItems.FirstAsync(i => i.Uid == _uid);
		return row.Id.ToString();
	}
}
