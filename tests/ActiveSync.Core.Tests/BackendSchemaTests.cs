using ActiveSync.Backends.Dav;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Jmap;
using ActiveSync.Backends.Sieve;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The self-describing config schema: every in-repo provider's fields must name real
///   bindable keys and carry the same defaults as its options class, since the UIs render
///   forms — and show "(default: X)" — from nothing else.
/// </summary>
public class BackendSchemaTests
{
	/// <summary>Provider, one of its roles, and the options type it binds for that role.</summary>
	public static TheoryData<string> SchemaRoles()
	{
		TheoryData<string> data = [];
		foreach ((IBackendProvider provider, _) in Providers)
		foreach (BackendRole role in provider.SupportedRoles)
			data.Add($"{provider.Name}:{role}");
		return data;
	}

	private static IEnumerable<(IBackendProvider Provider, Func<ProviderSettings, object> Bind)> Providers =>
	[
		(new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			s => s.Bind<ImapOptions>()),
		(new SmtpBackendProvider(NullLoggerFactory.Instance), s => s.Bind<SmtpOptions>()),
		(new CalDavBackendProvider(NullLoggerFactory.Instance), s => s.Bind<DavServerOptions>()),
		(new CardDavBackendProvider(NullLoggerFactory.Instance), s => s.Bind<DavServerOptions>()),
		(new JmapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			s => s.Bind<JmapOptions>()),
		(new SieveBackendProvider(NullLoggerFactory.Instance), s => s.Bind<SieveOptions>())
	];

	[Theory]
	[MemberData(nameof(SchemaRoles))]
	public void EveryFieldNamesABindableProperty(string providerRole)
	{
		(IBackendProvider provider, Func<ProviderSettings, object> bind) = Resolve(providerRole);
		BackendRole role = Enum.Parse<BackendRole>(providerRole.Split(':')[1]);

		foreach (BackendConfigField field in provider.DescribeConfiguration(role))
		{
			// A key the options type does not carry binds to nothing — the form would silently
			// collect a value the provider never reads.
			string key = field.Type == BackendFieldType.StringList ? field.Name + ":0" : field.Name;
			object options = bind(ProviderSettings.FromFlat(
				new Dictionary<string, string?> { [key] = SampleValue(field) }));
			Assert.NotNull(PropertyValue(options, field.Name));
		}
	}

	[Theory]
	[MemberData(nameof(SchemaRoles))]
	public void DeclaredDefaultsMatchTheOptionsClass(string providerRole)
	{
		(IBackendProvider provider, Func<ProviderSettings, object> bind) = Resolve(providerRole);
		BackendRole role = Enum.Parse<BackendRole>(providerRole.Split(':')[1]);
		object unset = bind(ProviderSettings.Empty);

		foreach (BackendConfigField field in provider.DescribeConfiguration(role))
		{
			if (field.Type == BackendFieldType.StringList)
				continue; // lists have no scalar default to render
			string? actual = PropertyValue(unset, field.Name)?.ToString();
			string? declared = field.Default;
			// null and "" both mean "nothing to show as a placeholder".
			Assert.True(string.Equals(actual ?? "", declared ?? "", StringComparison.OrdinalIgnoreCase),
				$"{providerRole} field {field.Name}: schema default '{declared}' " +
				$"but the options class yields '{actual}'.");
		}
	}

	[Theory]
	[MemberData(nameof(SchemaRoles))]
	public void EnumFieldsAcceptTheirOwnValues(string providerRole)
	{
		(IBackendProvider provider, _) = Resolve(providerRole);
		BackendRole role = Enum.Parse<BackendRole>(providerRole.Split(':')[1]);

		foreach (BackendConfigField field in provider.DescribeConfiguration(role))
		{
			if (field.Type != BackendFieldType.Enum)
				continue;
			Assert.NotEmpty(field.EnumValues!);
			foreach (string value in field.EnumValues!)
				Assert.Null(BackendConfigValidation.CheckValue(field, value));
		}
	}

	[Fact]
	public void ProvidersWithoutASchemaDescribeNothing()
	{
		// The default interface member: a plugin built against the older contract stays valid
		// and simply falls back to the raw key/value editors.
		IBackendProvider schemaless = new SchemalessProvider();
		Assert.Empty(schemaless.DescribeConfiguration(BackendRole.Contacts));
	}

	[Fact]
	public void ImapSchemaCoversTheKeysItsValidationChecks()
	{
		IBackendProvider imap = new ImapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance);
		string[] names = [.. imap.DescribeConfiguration(BackendRole.MailStore).Select(f => f.Name)];
		Assert.Contains("Host", names);
		Assert.Contains("Port", names);
		Assert.Contains("Security", names);
		Assert.Contains("CaCertificatePath", names);
	}

	[Fact]
	public void RequiredFieldWithoutAValueIsRejected_ButAnUnsetOptionalOneIsNot()
	{
		IBackendProvider caldav = new CalDavBackendProvider(NullLoggerFactory.Instance);
		IReadOnlyList<BackendConfigField> fields = caldav.DescribeConfiguration(BackendRole.Calendar);

		Assert.Contains(BackendConfigValidation.ValidateFields(fields, new Dictionary<string, string?>()),
			e => e.Field == "BaseUrl");
		Assert.Empty(BackendConfigValidation.ValidateFields(fields,
			new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" }));
	}

	[Fact]
	public void BadValuesAreRejectedPerField()
	{
		IBackendProvider imap = new ImapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance);
		IReadOnlyList<BackendConfigField> fields = imap.DescribeConfiguration(BackendRole.MailStore);

		IReadOnlyList<BackendFieldError> errors = BackendConfigValidation.ValidateFields(fields,
			new Dictionary<string, string?>
			{
				["Host"] = "mail.example.com",
				["Port"] = "99999",
				["UseSsl"] = "yes-please",
				["Security"] = "Quantum"
			});

		Assert.Equal(["Port", "Security", "UseSsl"], errors.Select(e => e.Field).Order());
	}

	[Fact]
	public void UnknownKeysPassUntouched()
	{
		// Plugin providers may describe only part of their surface; the rest must stay settable.
		IBackendProvider imap = new ImapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance);
		Assert.Empty(BackendConfigValidation.ValidateFields(
			imap.DescribeConfiguration(BackendRole.MailStore),
			new Dictionary<string, string?> { ["Host"] = "mail.example.com", ["FutureKnob"] = "42" }));
	}

	[Fact]
	public void ValidateAlsoRunsTheProvidersOwnChecks()
	{
		IBackendProvider caldav = new CalDavBackendProvider(NullLoggerFactory.Instance);
		// Shape is fine (an absolute URL), but the provider rejects the shared-collection entry.
		IReadOnlyList<BackendFieldError> errors = BackendConfigValidation.Validate(
			caldav, BackendRole.Calendar, new Dictionary<string, string?>
			{
				["BaseUrl"] = "https://dav.example.com",
				["SharedCollections:0"] = "not a path"
			});
		Assert.Contains(errors, e => e.Field is null && e.Message.Contains("SharedCollections"));
	}

	[Fact]
	public void FromFlatMaterializesScalarsAndLists()
	{
		ProviderSettings settings = ProviderSettings.FromFlat(new Dictionary<string, string?>
		{
			["BaseUrl"] = "https://dav.example.com",
			["SharedCollections:0"] = "/dav/cal/team/",
			["SharedCollections:1"] = "/dav/cal/ops/|ro",
			["HomeSetPath"] = null
		});

		DavServerOptions options = settings.Bind<DavServerOptions>();
		Assert.Equal("https://dav.example.com", options.BaseUrl);
		Assert.Equal(["/dav/cal/team/", "/dav/cal/ops/|ro"], options.SharedCollections);
		Assert.Null(options.HomeSetPath);
	}

	[Fact]
	public void ListRootStripsNumericTails()
	{
		Assert.Equal("SharedCollections", BackendConfigValidation.ListRoot("SharedCollections:0"));
		Assert.Equal("Host", BackendConfigValidation.ListRoot("Host"));
		Assert.Equal("A", BackendConfigValidation.ListRoot("A:1:2"));
	}

	private static (IBackendProvider Provider, Func<ProviderSettings, object> Bind) Resolve(string providerRole)
	{
		string name = providerRole.Split(':')[0];
		return Providers.Single(p => p.Provider.Name == name);
	}

	private static string SampleValue(BackendConfigField field)
	{
		return field.Type switch
		{
			BackendFieldType.Int => "7",
			BackendFieldType.Bool => "true",
			BackendFieldType.Enum => field.EnumValues![0],
			BackendFieldType.Url => "https://example.com",
			_ => "x"
		};
	}

	/// <summary>Reads a property by config-key name (config binding is case-insensitive).</summary>
	private static object? PropertyValue(object options, string name)
	{
		return options.GetType()
			.GetProperties()
			.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			?.GetValue(options);
	}

	/// <summary>Stands in for an out-of-repo plugin built before the schema existed.</summary>
	private sealed class SchemalessProvider : IBackendProvider
	{
		public string Name => "schemaless";
		public IReadOnlySet<BackendRole> SupportedRoles => new HashSet<BackendRole> { BackendRole.Contacts };

		public IBackendConnection CreateConnection(BackendConnectionContext context)
		{
			throw new NotSupportedException();
		}

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
		{
		}

		public string DescribeRole(BackendRole role, ProviderSettings settings) => "schemaless";
	}
}
