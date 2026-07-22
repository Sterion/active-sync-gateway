using System.Security.Cryptography;
using System.Xml.Linq;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Auth;

/// <summary>
///   DataProtection key-ring storage in the state database, so web sessions survive restarts
///   and validate on every replica. The official EF-Core repository package would drag an
///   ASP.NET dependency into Core's packed plugin contract, so this small repository writes
///   the same row shape itself. Key XML is sealed with the Encryption master key when one is
///   configured — a database dump alone cannot forge web sessions. Reads/writes are rare
///   (key-ring load + ~90-day rotations), so a fresh context per call is fine.
/// </summary>
internal sealed class DbXmlRepository(
	ISyncDbContextFactory contextFactory,
	IOptions<ActiveSyncOptions> options) : IXmlRepository
{
	public IReadOnlyCollection<XElement> GetAllElements()
	{
		using SyncDbContext db = contextFactory.CreateDbContext();
		List<DataProtectionKeyEntry> rows = db.DataProtectionKeys.AsNoTracking().ToList();
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Value.Encryption, out _);
		try
		{
			List<XElement> elements = new();
			foreach (DataProtectionKeyEntry row in rows)
			{
				if (string.IsNullOrEmpty(row.Xml))
					continue;
				string xml = row.Xml;
				if (SecretValue.IsSealed(xml))
				{
					// Wrong/missing master key: skip the row — DataProtection mints a fresh
					// key (users re-login once) instead of the whole web UI failing.
					if (key is null || !SecretValue.TryUnseal(xml, key, out string? plain, out _))
						continue;
					xml = plain!;
				}

				elements.Add(XElement.Parse(xml));
			}

			return elements;
		}
		finally
		{
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
		}
	}

	public void StoreElement(XElement element, string friendlyName)
	{
		string xml = element.ToString(SaveOptions.DisableFormatting);
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Value.Encryption, out _);
		try
		{
			if (key is not null)
				xml = SecretValue.Seal(xml, key);
		}
		finally
		{
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
		}

		using SyncDbContext db = contextFactory.CreateDbContext();
		db.DataProtectionKeys.Add(new DataProtectionKeyEntry { FriendlyName = friendlyName, Xml = xml });
		db.SaveChanges();
	}
}
