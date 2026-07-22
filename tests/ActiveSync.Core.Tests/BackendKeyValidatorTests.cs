using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B24 — validating a single backend key in isolation. When the key is <c>Provider</c>, it is not
///   enough that the registry can serve the role: the settings ALREADY stored under that role must
///   also satisfy the incoming provider's schema, otherwise the switch is accepted over a section
///   shaped for the old provider and only surfaces at the next restart.
/// </summary>
public sealed class BackendKeyValidatorTests
{
	private static BackendProviderRegistry Registry() =>
		new(
		[
			new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SmtpBackendProvider(NullLoggerFactory.Instance),
		], NullLogger<BackendProviderRegistry>.Instance);

	private static IConfiguration Config(Dictionary<string, string?> values) =>
		new ConfigurationBuilder().AddInMemoryCollection(values).Build();

	// The bug: a value already stored under the role is mis-shaped for the incoming provider (a
	// non-numeric Port), yet the switch used to be accepted because imap CAN serve MailStore.
	[Fact]
	public void ProviderChange_OverAMisShapedStoredValue_IsRejected()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
			["ActiveSync:Backends:MailStore:Port"] = "not-a-number",
		});

		string? error = BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "imap");
		Assert.NotNull(error);
	}

	// Present values that are well-shaped for the new provider are accepted...
	[Fact]
	public void ProviderChange_OverWellShapedStoredValues_IsAccepted()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
			["ActiveSync:Backends:MailStore:Port"] = "993",
		});

		Assert.Null(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "imap"));
	}

	// ...and a still-incomplete section (a required field the operator hasn't set yet) does NOT block
	// assigning the provider — completeness is checked at startup, not when the provider is chosen.
	[Fact]
	public void ProviderChange_OverAnIncompleteSection_IsAccepted()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Port"] = "993", // no Host yet
		});

		Assert.Null(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "imap"));
	}

	// Existing behaviour preserved: a provider that cannot serve the role is still rejected outright.
	[Fact]
	public void ProviderChange_ToAProviderThatCannotServeTheRole_IsRejected()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		});

		Assert.NotNull(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "smtp"));
	}

	// B25 — the provider's schema is authoritative for secret masking, both ways: a Secret-typed field
	// whose NAME the heuristic would miss is masked; a String field whose name the heuristic would
	// (wrongly) flag is not.
	[Fact]
	public void IsSecretLeaf_ConsultsTheProviderSchema_NotJustTheNameHeuristic()
	{
		BackendProviderRegistry registry = new(
			[new SchemaProvider("plug", BackendRole.MailStore)], NullLogger<BackendProviderRegistry>.Instance);
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "plug",
		});

		// Schema says secret even though "AuthBlob" matches no heuristic marker.
		Assert.True(BackendKeyValidator.IsSecretLeaf(registry, effective, "ActiveSync:Backends:MailStore:AuthBlob"));
		// Schema says NOT secret even though "Token" matches the heuristic.
		Assert.False(BackendKeyValidator.IsSecretLeaf(registry, effective, "ActiveSync:Backends:MailStore:Token"));
	}

	[Fact]
	public void IsSecretLeaf_FallsBackToTheNameHeuristic_WhenNoFieldClaimsTheLeaf()
	{
		BackendProviderRegistry registry = new(
			[new SchemaProvider("plug", BackendRole.MailStore)], NullLogger<BackendProviderRegistry>.Instance);
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "plug",
		});

		// No field named "ClientSecret" in the schema → heuristic decides, and it is a secret name.
		Assert.True(BackendKeyValidator.IsSecretLeaf(registry, effective, "ActiveSync:Backends:MailStore:ClientSecret"));
		Assert.False(BackendKeyValidator.IsSecretLeaf(registry, effective, "ActiveSync:Backends:MailStore:Folder"));
	}

	/// <summary>A minimal provider whose only interesting surface is a self-describing schema.</summary>
	private sealed class SchemaProvider(string name, params BackendRole[] roles) : IBackendProvider
	{
		public string Name => name;
		public IReadOnlySet<BackendRole> SupportedRoles { get; } = new HashSet<BackendRole>(roles);

		public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role) =>
		[
			new BackendConfigField("AuthBlob", "Auth blob", BackendFieldType.Secret),
			new BackendConfigField("Token", "Token", BackendFieldType.String),
		];

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => $"{name} fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
