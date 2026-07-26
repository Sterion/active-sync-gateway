using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using ActiveSync.Contracts;
using ActiveSync.Protocol;

namespace ActiveSync.Core.Tests;

/// <summary>
///   A forcing function, not a description: the public surface of the two PUBLISHED contract
///   assemblies is snapshotted here, keyed by the contract version it belongs to. Changing that
///   surface without raising <c>$(ContractVersionMinor)</c> in <c>Directory.Build.props</c> fails
///   this test, and the failure message says exactly what to do.
///   <para>
///     Why it exists: the contract version gates every out-of-repo plugin (the loader demands an
///     exact major.minor match), so a surface change that ships under the old version silently
///     breaks plugins that still claim compatibility. Documentation cannot enforce that — this
///     can. See the <c>ContractVersion</c> block in <c>Directory.Build.props</c>.
///   </para>
///   <para>
///     The history block is APPEND-ONLY. Each line pins the surface hash for a contract version
///     that has existed, so the only way to make a changed surface pass is to introduce a NEW
///     version line — which requires bumping the property. Editing an existing line defeats the
///     entire guard; don't.
///   </para>
/// </summary>
public sealed class ContractSurfaceApprovalTests
{
	/// <summary>Set to 1 and re-run to rewrite the approved file after a DELIBERATE change.</summary>
	private const string ApproveVariable = "EAS_APPROVE_CONTRACT_SURFACE";

	private const string HistoryHeader = "=== VERSION HISTORY (append-only) ===";
	private const string SurfaceHeader = "=== SURFACE ===";

	private static string ApprovedPath =>
		Path.Combine(RepositoryRoot(), "tests", "ActiveSync.Core.Tests", "ContractSurface.approved.txt");

	[Fact]
	public void ContractSurface_MatchesTheApprovedSnapshotForThisContractVersion()
	{
		string surface = BuildSurface();
		string hash = Sha256(surface);
		Version current = ContractVersion.Current;
		string version = current.ToString(2);

		if (Environment.GetEnvironmentVariable(ApproveVariable) == "1")
		{
			Approve(version, hash, surface);
			return;
		}

		Assert.True(File.Exists(ApprovedPath),
			$"No approved contract surface at {ApprovedPath}. Generate it with {ApproveVariable}=1.");

		string approved = File.ReadAllText(ApprovedPath).Replace("\r\n", "\n");
		Dictionary<string, string> history = ParseHistory(approved);

		if (history.TryGetValue(version, out string? approvedHash))
		{
			if (approvedHash == hash)
				return;

			// The surface moved while the contract version stood still. This is the case the
			// whole file exists for.
			Assert.Fail(
				$"""
				 The PUBLIC SURFACE of ActiveSync.Contracts / ActiveSync.Protocol changed, but the
				 contract version is still {version}.

				 Every out-of-repo plugin declares an exact contract major.minor and is refused by
				 the loader if it does not match, so shipping a changed surface under an unchanged
				 version silently breaks plugins that still claim compatibility.

				 DO THIS:
				   1. Raise <ContractVersionMinor> in Directory.Build.props ({current.Major}.{current.Minor}
				      -> {current.Major}.{current.Minor + 1}). Minor is the right component: every contract
				      change is breaking by policy, and minor absorbs it.
				      *** Raising <ContractVersionMajor> is a HUMAN decision. Do not raise it
				          unless you were explicitly asked to. ***
				   2. Update the literal in
				      ContractSurfaceTests.ContractVersion_IsTheExpectedSurfaceVersion.
				   3. Re-run this test with {ApproveVariable}=1 to append the new version to the
				      approved snapshot.

				 If you did NOT mean to change the contract surface, revert the change instead —
				 moving a type into ActiveSync.Contracts or ActiveSync.Protocol also makes it
				 permanently MIT-licensed (see LICENSE).

				 Approved hash for {version}: {approvedHash}
				 Actual hash:                 {hash}
				 """);
		}

		// A version with no recorded surface: the property was raised but the snapshot was not
		// regenerated. Harmless, but leaves the guard with nothing to compare next time.
		Assert.Fail(
			$"""
			 Contract version {version} has no approved surface snapshot — the version was raised
			 without regenerating it. Re-run this test with {ApproveVariable}=1 and commit the
			 updated {Path.GetFileName(ApprovedPath)}.

			 Known versions: {string.Join(", ", history.Keys.OrderBy(k => k, StringComparer.Ordinal))}
			 """);
	}

	/// <summary>
	///   The approved file is only meaningful if it describes THIS build's contract version, so the
	///   header records it and this asserts the two agree.
	/// </summary>
	[Fact]
	public void ApprovedSnapshot_DescribesTheCurrentContractVersion()
	{
		if (!File.Exists(ApprovedPath))
			return;

		string approved = File.ReadAllText(ApprovedPath).Replace("\r\n", "\n");
		string marker = $"# Current contract version: {ContractVersion.Current.ToString(2)}";
		Assert.Contains(marker, approved, StringComparison.Ordinal);
	}

	private static void Approve(string version, string hash, string surface)
	{
		Dictionary<string, string> history = File.Exists(ApprovedPath)
			? ParseHistory(File.ReadAllText(ApprovedPath).Replace("\r\n", "\n"))
			: new Dictionary<string, string>(StringComparer.Ordinal);
		history[version] = hash;

		StringBuilder file = new();
		file.Append("# Public surface of the PUBLISHED contract assemblies (ActiveSync.Contracts,\n");
		file.Append("# ActiveSync.Protocol). Generated — do not hand-edit.\n");
		file.Append("#\n");
		file.Append("# Regenerate after a DELIBERATE contract change:\n");
		file.Append($"#   {ApproveVariable}=1 dotnet test --filter FullyQualifiedName~ContractSurfaceApprovalTests\n");
		file.Append("#\n");
		file.Append("# The history below is APPEND-ONLY: each line pins the surface a contract version\n");
		file.Append("# shipped with. Editing an existing line defeats the guard that a surface change\n");
		file.Append("# must come with a version bump.\n");
		file.Append($"# Current contract version: {version}\n\n");

		file.Append(HistoryHeader).Append('\n');
		foreach ((string key, string value) in history.OrderBy(kv => kv.Key, StringComparer.Ordinal))
			file.Append(key).Append("  ").Append(value).Append('\n');

		file.Append('\n').Append(SurfaceHeader).Append('\n').Append(surface);
		File.WriteAllText(ApprovedPath, file.ToString());
	}

	private static Dictionary<string, string> ParseHistory(string approved)
	{
		Dictionary<string, string> history = new(StringComparer.Ordinal);
		bool inHistory = false;
		foreach (string line in approved.Split('\n'))
		{
			if (line.StartsWith(HistoryHeader, StringComparison.Ordinal)) { inHistory = true; continue; }
			if (line.StartsWith(SurfaceHeader, StringComparison.Ordinal)) break;
			if (!inHistory || line.Length == 0 || line[0] == '#') continue;

			string[] parts = line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 2)
				history[parts[0]] = parts[1];
		}

		return history;
	}

	private static string Sha256(string text) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	/// <summary>
	///   Renders every public and protected member of both assemblies in a stable order. Deliberately
	///   not a full IL signature: it catches added, removed, renamed and retyped members, which is
	///   what a plugin author feels. Compiler-generated members and accessor methods are skipped —
	///   they are noise, and the property or event they belong to is listed anyway.
	/// </summary>
	private static string BuildSurface()
	{
		Assembly[] assemblies = [typeof(IGatewayPlugin).Assembly, typeof(EasVersion).Assembly];
		StringBuilder surface = new();

		foreach (Assembly assembly in assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
		{
			surface.Append("assembly ").Append(assembly.GetName().Name).Append('\n');
			foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
			{
				surface.Append("  ").Append(DescribeType(type)).Append('\n');
				foreach (string member in DescribeMembers(type).OrderBy(m => m, StringComparer.Ordinal))
					surface.Append("    ").Append(member).Append('\n');
			}
		}

		return surface.ToString();
	}

	private static string DescribeType(Type type)
	{
		string kind = type.IsInterface ? "interface"
			: type.IsEnum ? "enum"
			: type.IsValueType ? "struct"
			: "class";
		StringBuilder text = new();
		text.Append(kind).Append(' ').Append(TypeName(type));
		if (type is { IsClass: true, BaseType: not null } && type.BaseType != typeof(object))
			text.Append(" : ").Append(TypeName(type.BaseType));
		string[] interfaces = [.. type.GetInterfaces().Select(TypeName).OrderBy(n => n, StringComparer.Ordinal)];
		if (interfaces.Length > 0)
			text.Append(" implements ").Append(string.Join(", ", interfaces));
		return text.ToString();
	}

	private static IEnumerable<string> DescribeMembers(Type type)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
		                                                | BindingFlags.Instance | BindingFlags.Static
		                                                | BindingFlags.DeclaredOnly;

		foreach (FieldInfo field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily))
		{
			// An enum also carries the synthetic instance field `value__`, which is not a literal
			// and throws from GetRawConstantValue. Only the members are surface.
			if (Generated(field.Name) || (type.IsEnum && !field.IsLiteral)) continue;
			string value = field.IsLiteral && field.GetRawConstantValue() is { } constant
				? " = " + Convert.ToString(constant, CultureInfo.InvariantCulture)
				: "";
			yield return $"field {TypeName(field.FieldType)} {field.Name}{value}";
		}

		foreach (PropertyInfo property in type.GetProperties(flags))
		{
			MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
			if (accessor is null || !(accessor.IsPublic || accessor.IsFamily) || Generated(property.Name)) continue;
			string ops = (property.GetMethod is not null ? "get;" : "") + (property.SetMethod is not null ? "set;" : "");
			yield return $"property {TypeName(property.PropertyType)} {property.Name} {{ {ops} }}";
		}

		foreach (EventInfo evt in type.GetEvents(flags))
		{
			if (Generated(evt.Name)) continue;
			yield return $"event {TypeName(evt.EventHandlerType!)} {evt.Name}";
		}

		foreach (ConstructorInfo ctor in type.GetConstructors(flags).Where(c => c.IsPublic || c.IsFamily))
			yield return $"ctor ({Parameters(ctor)})";

		foreach (MethodInfo method in type.GetMethods(flags).Where(m => m.IsPublic || m.IsFamily))
		{
			if (Generated(method.Name) || Accessor(method.Name)) continue;
			yield return $"method {TypeName(method.ReturnType)} {method.Name}({Parameters(method)})";
		}
	}

	private static string Parameters(MethodBase method) =>
		string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

	private static bool Generated(string name) => name.Contains('<', StringComparison.Ordinal);

	private static bool Accessor(string name) =>
		name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)
		|| name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal);

	private static string TypeName(Type type)
	{
		if (type.IsGenericType)
		{
			string root = (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0];
			return $"{root}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
		}

		return type.FullName ?? type.Name;
	}

	/// <summary>Walks up from the test binaries to the directory holding ActiveSync.slnx.</summary>
	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ActiveSync.slnx")))
			directory = directory.Parent;

		Assert.NotNull(directory);
		return directory.FullName;
	}
}
