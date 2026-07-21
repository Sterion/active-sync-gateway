using ActiveSync.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   <c>eas tls</c> — show the gateway's active HTTPS certificate: which mode is serving
///   (self-signed / mounted file / off), and the certificate's subject, SANs, validity,
///   fingerprint and key. Read-only; the paths are set with
///   <c>eas config set ActiveSync:Tls:CertificatePath …</c>.
/// </summary>
internal sealed class TlsCommand(IAnsiConsole terminal) : AsyncCommand<TlsCommand.Settings>
{
	public sealed class Settings : CommandSettings;

	protected override async Task<int> ExecuteAsync(
		CommandContext context, Settings settings, CancellationToken cancellationToken)
	{
		// Lean provider: TLS is independent of mail configuration, so this works even on an
		// unconfigured or broken gateway.
		ServiceProvider? services = await CliServices.TryCreateLeanAsync();
		if (services is null)
			return 1;
		await using ServiceProvider _ = services;

		TlsCertificateInfo info = await services.GetRequiredService<TlsCertificateResolver>()
			.DescribeAsync(services.GetRequiredService<ILoggerFactory>().CreateLogger("eas.tls"),
				cancellationToken);

		string status = info.Enabled ? $"serving on :{info.Port}" : "off (terminate TLS in front)";
		string source = info.Source switch
		{
			TlsCertificateSource.SelfSigned => "self-signed (stored in the database)",
			TlsCertificateSource.External => $"mounted file ({info.CertificatePath})",
			_ => "disabled",
		};

		Table table = new Table().Border(TableBorder.Rounded).AddColumn("Field").AddColumn("Value");
		table.AddRow("Status", status);
		table.AddRow("Source", source);
		if (info.Subject is not null)
		{
			table.AddRow("Subject", info.Subject);
			table.AddRow("Issuer", info.Issuer ?? "");
			table.AddRow("Subject alt names",
				info.SubjectAlternativeNames.Count > 0 ? string.Join(", ", info.SubjectAlternativeNames) : "—");
			table.AddRow("Valid from", Format(info.NotBeforeUtc));
			table.AddRow("Valid until", Format(info.NotAfterUtc));
			table.AddRow("Key",
				info.KeyAlgorithm is null ? "—" : $"{info.KeyAlgorithm}{(info.KeySize is { } k ? $" {k}-bit" : "")}");
			table.AddRow("SHA-256", info.Fingerprint ?? "");
		}

		terminal.Write(table);

		if (info.Error is not null)
			terminal.MarkupLineInterpolated($"[red]{info.Error}[/]");

		if (info.NotAfterUtc is { } expiry)
		{
			int days = (int)Math.Floor((expiry - DateTime.UtcNow).TotalDays);
			if (days < 0)
				terminal.MarkupLineInterpolated($"[red]Certificate EXPIRED {-days} day(s) ago.[/]");
			else if (days <= 30)
				terminal.MarkupLineInterpolated($"[yellow]Certificate expires in {days} day(s).[/]");
		}

		return 0;
	}

	private static string Format(DateTime? utc) =>
		utc is { } value
			? value.ToString("yyyy-MM-dd HH:mm:ss' UTC'", System.Globalization.CultureInfo.InvariantCulture)
			: "—";
}
