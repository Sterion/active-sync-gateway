using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D24: a malformed client-supplied date must not surface as an uncontrolled exception that
///   fails the whole Sync. Every client-input site parses defensively (like ParseUid /
///   base64 handling elsewhere) and skips the bad field rather than throwing.
/// </summary>
public class ClientDateParsingTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;
	private static readonly XNamespace Tasks = EasNamespaces.Tasks;

	private static XElement AppData(params object[] content)
	{
		return new XElement("ApplicationData", content);
	}

	[Fact]
	public void Calendar_MalformedStartTime_DoesNotThrow_AndOmitsStart()
	{
		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Broken"),
			new XElement(Cal + "StartTime", "not-a-date"),
			new XElement(Cal + "EndTime", "also-bad")), Guid.NewGuid().ToString(), null);

		Assert.Contains("SUMMARY:Broken", ics);
		// A garbage StartTime must not become a corrupt DTSTART.
		Assert.DoesNotContain("DTSTART:not-a-date", ics);
	}

	[Fact]
	public void Contact_MalformedBirthday_DoesNotThrow_AndOmitsBday()
	{
		string vcard = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FileAs", "Doe, Jane"),
			new XElement(Contacts + "Birthday", "31-02-1990")), Guid.NewGuid().ToString());

		Assert.DoesNotContain("BDAY", vcard);
	}

	[Fact]
	public void Task_MalformedDueDate_DoesNotThrow()
	{
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "Broken task"),
			new XElement(Tasks + "DueDate", "garbage")), Guid.NewGuid().ToString(), null);

		Assert.Contains("SUMMARY:Broken task", ics);
	}

	[Fact]
	public void Calendar_MalformedExceptionStartTime_Skipped_NotThrown()
	{
		string existing = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T091500Z"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "1"),
				new XElement(Cal + "DayOfWeek", "62"))), "u-1", null);

		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Exceptions",
				new XElement(Cal + "Exception",
					new XElement(Cal + "Deleted", "1"),
					new XElement(Cal + "ExceptionStartTime", "bogus")))), "u-1", existing);

		Assert.Contains("RRULE", updated);
	}
}
