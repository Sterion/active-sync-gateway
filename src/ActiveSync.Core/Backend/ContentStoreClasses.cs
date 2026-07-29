using ActiveSync.Contracts;
using ActiveSync.Protocol;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Derives a store's EAS content class from WHICH class alias interface it implements —
///   the store never declares a class (a declaration could only agree with the alias or be a
///   bug), and the wire strings are host knowledge the contract deliberately does not carry.
/// </summary>
public static class ContentStoreClasses
{
	/// <summary>
	///   The EAS class string for a store. Throws when the store implements no class alias, or
	///   more than one — connection creation surfaces that as a provider bug (the contract
	///   requires exactly one).
	/// </summary>
	/// <param name="store">The store to classify.</param>
	/// <returns>The EAS class string ("Email", "Calendar", "Contacts", "Tasks", "Notes").</returns>
	public static string EasClassOf(IContentStore store)
	{
		string? easClass = null;

		void Claim(string value)
		{
			if (easClass is not null)
				throw new InvalidOperationException(
					$"Store {store.GetType().FullName} implements more than one content-class alias " +
					$"({easClass} and {value}); a store must implement exactly one.");
			easClass = value;
		}

		if (store is IMailStore)
			Claim(EasClass.Email);
		if (store is ICalendarStore)
			Claim(EasClass.Calendar);
		if (store is ITaskStore)
			Claim(EasClass.Tasks);
		if (store is IContactStore)
			Claim(EasClass.Contacts);
		if (store is INotesStore)
			Claim(EasClass.Notes);

		return easClass ?? throw new InvalidOperationException(
			$"Store {store.GetType().FullName} implements none of the content-class alias interfaces " +
			"(IMailStore/ICalendarStore/ITaskStore/IContactStore/INotesStore); a store must implement exactly one.");
	}
}
