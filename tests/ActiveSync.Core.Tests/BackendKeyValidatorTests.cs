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
///   Validating a single backend key in isolation. When the key is <c>Provider</c>, it is not
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

	[Theory]
	[InlineData("Password")]
	[InlineData("UserName")]
	public void AGlobalBackendCredential_IsRefused_BecauseNothingReadsIt(string leaf)
	{
		// It used to be accepted, stored, and even masked as a secret in `eas config list` — while
		// being read by nothing at all. Credentials are RESOLVED per user and handed to the provider
		// as BackendCredentials; no provider takes them from settings. So this looked exactly like
		// configuring one shared mail credential for everyone and silently was not.
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		});

		string? error = BackendKeyValidator.Validate(
			Registry(), effective, $"ActiveSync:Backends:MailStore:{leaf}", "hunter2");

		Assert.NotNull(error);
		Assert.Contains("no provider reads it", error);
		Assert.Contains("per user", error);
	}

	[Fact]
	public void AGlobalBackendCredential_IsRefused_EvenBeforeAProviderIsAssigned()
	{
		// The early "no provider yet, nothing to judge" exit must not become a way in.
		Assert.NotNull(BackendKeyValidator.Validate(
			Registry(), Config(new Dictionary<string, string?>()),
			"ActiveSync:Backends:MailStore:Password", "hunter2"));
	}

	[Fact]
	public void ARealProviderSetting_IsStillAccepted()
	{
		// The refusal keys off "no provider field claims this leaf", not off the name alone: a
		// plugin that genuinely describes a Password setting still owns that name.
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
		});

		Assert.Null(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Host", "imap.example"));
	}

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

	// The provider's schema is authoritative for secret masking, both ways: a Secret-typed field
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

	// A backend-section WRITE is never re-validated against the declared users. Here, a config
	// user (bob) has NO Oof provider override of his own, only a per-role Settings entry ("Legacy")
	// that is merged onto whichever provider the GLOBAL Oof role currently names (UserResolver's
	// "inherit global settings only when the provider is unchanged" rule ties inheritance to the
	// override, not the value — with no override, the merge always follows the current global
	// provider). Reassigning the global Oof provider from one that tolerates "Legacy" to one that
	// rejects it is accepted today by the per-field/per-provider-shape check alone, even though the
	// user's own merged settings are now invalid under the new provider.
	[Fact]
	public void WritingAGlobalProviderChange_ThatBreaksADeclaredUsersMergedSettings_IsRejected()
	{
		BackendProviderRegistry registry = new(
			[new OldOofProvider(), new NewOofProvider()], NullLogger<BackendProviderRegistry>.Instance);

		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Oof:Provider"] = "old-oof",
			["ActiveSync:Users:bob:Backends:Oof:Settings:Legacy"] = "1",
		});

		// Sanity: the configuration as it stands today is valid — old-oof tolerates "Legacy".
		Assert.Null(BackendKeyValidator.Validate(registry, effective, "ActiveSync:Backends:Oof:Provider", "old-oof"));

		string? error = BackendKeyValidator.Validate(registry, effective, "ActiveSync:Backends:Oof:Provider", "new-oof");
		Assert.NotNull(error);
		Assert.Contains("Legacy", error);
	}

	// The same write must still be accepted for a user who never touches the field the new
	// provider rejects — the check must not become a blanket "any user exists" refusal.
	[Fact]
	public void WritingAGlobalProviderChange_ThatDoesNotAffectAnyDeclaredUser_IsAccepted()
	{
		BackendProviderRegistry registry = new(
			[new OldOofProvider(), new NewOofProvider()], NullLogger<BackendProviderRegistry>.Instance);

		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Oof:Provider"] = "old-oof",
			["ActiveSync:Users:bob:Backends:Oof:Settings:Modern"] = "1",
		});

		Assert.Null(BackendKeyValidator.Validate(registry, effective, "ActiveSync:Backends:Oof:Provider", "new-oof"));
	}

	/// <summary>Tolerates any settings — stands in for the currently configured provider before a global provider swap.</summary>
	private sealed class OldOofProvider : IBackendProvider
	{
		public string Name => "old-oof";
		public IReadOnlySet<BackendRole> SupportedRoles { get; } = new HashSet<BackendRole> { BackendRole.Oof };
		public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role) => [];
		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "old-oof";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	/// <summary>Rejects the "Legacy" setting the old provider tolerated — the "after" side.</summary>
	private sealed class NewOofProvider : IBackendProvider
	{
		public string Name => "new-oof";
		public IReadOnlySet<BackendRole> SupportedRoles { get; } = new HashSet<BackendRole> { BackendRole.Oof };
		public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role) => [];

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
		{
			if (settings.Section["Legacy"] is not null)
				failures.Add($"new-oof ({role}): Legacy is not supported by new-oof.");
		}

		public string DescribeRole(BackendRole role, ProviderSettings settings) => "new-oof";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();
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
