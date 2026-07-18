using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Email Categories ↔ IMAP keywords: system keywords never surface as categories, user
///   keywords do (sorted, stable), and keyword-less messages emit no Categories element.
/// </summary>
public class MailConverterCategoryTests
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	private static MimeMessage Message()
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "categorized";
		message.Body = new TextPart("plain") { Text = "body" };
		return message;
	}

	private static List<XElement> Convert(IReadOnlyCollection<string>? keywords)
	{
		return MailConverter.ToApplicationData(
			Message(),
			new MailConverter.MessageFlags(true, false, false, false, keywords),
			new BodyPreference(1, null, false),
			_ => "ref");
	}

	[Fact]
	public void UserKeywords_SurfaceAsCategories_SortedAndFiltered()
	{
		List<XElement> data = Convert(["zebra", "$Forwarded", "Work", "\\Seen", "NonJunk"]);

		XElement categories = data.Single(e => e.Name == Email + "Categories");
		Assert.Equal(["Work", "zebra"],
			categories.Elements(Email + "Category").Select(c => c.Value).ToArray());
	}

	[Fact]
	public void SystemKeywordsOnly_EmitNoCategoriesElement()
	{
		List<XElement> data = Convert(["$Forwarded", "$MDNSent", "Junk", "$NotJunk", "\\Flagged"]);

		Assert.DoesNotContain(data, e => e.Name == Email + "Categories");
	}

	[Fact]
	public void NoKeywords_EmitNoCategoriesElement()
	{
		Assert.DoesNotContain(Convert(null), e => e.Name == Email + "Categories");
		Assert.DoesNotContain(Convert([]), e => e.Name == Email + "Categories");
	}

	[Fact]
	public void CategoryKeywords_FilterIsCaseInsensitive_AndOrderStable()
	{
		IReadOnlyList<string> filtered = MailConverter.CategoryKeywords(
			["b-tag", "JUNK", "$forwarded", "A-tag", "\\Recent"]);

		// Sorted output keeps revision strings stable regardless of server keyword order.
		Assert.Equal(["A-tag", "b-tag"], filtered);
	}
}
