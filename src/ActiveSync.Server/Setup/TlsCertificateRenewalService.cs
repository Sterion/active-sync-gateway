using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Keeps a self-signed HTTPS certificate from expiring inside a long-running process.
///   <see cref="GatewayCertificateStore.GetOrCreateAsync" /> already renews ahead of expiry once a
///   stored certificate enters its renewal window — but until this service existed nothing ever
///   called it again after the single startup load in <c>InitializeAsync</c>, so a gateway with
///   more than ~367 days of uptime crossed into hard expiry with no operator signal (the 397-day
///   validity cap landed; the renewal did not). This ticks periodically, asks the store whether a
///   renewal is due, and — only when the certificate actually changed — swaps the
///   <see cref="CertificateHolder" /> instance Kestrel's HTTPS selector reads, logging the rotation.
///   The previous certificate is disposed after a short grace period rather than immediately, so an
///   in-flight handshake that already read the old instance is not disposed out from under it.
///   Deliberately scoped to self-signed serving only: an operator-supplied file
///   (<see cref="TlsOptions.CertificatePath" />) is documented as restart-tier — a rotated mount
///   takes effect on the next restart, matching Kubernetes mount + Kestrel behavior — so this
///   service no-ops when one is configured, or when TLS is off.
/// </summary>
public sealed class TlsCertificateRenewalService(
	CertificateHolder holder,
	GatewayCertificateStore certificateStore,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<TlsCertificateRenewalService> logger,
	TimeSpan? tickInterval = null,
	TimeSpan? disposeGracePeriod = null) : BackgroundService
{
	private readonly TimeSpan _tickInterval = tickInterval ?? TimeSpan.FromDays(1);
	private readonly TimeSpan _disposeGracePeriod = disposeGracePeriod ?? TimeSpan.FromSeconds(30);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TlsOptions tls = options.CurrentValue.Tls;
		if (!tls.Enabled || !string.IsNullOrWhiteSpace(tls.CertificatePath))
			return; // HTTPS off, or an operator-supplied file — restart-only rotation (see class doc).

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await CheckAndRenewAsync(stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break; // host shutdown — the only cancellation that should stop the check
			}
			catch (Exception ex)
			{
				// Must not stop the loop — a transient DB fault on one tick would otherwise freeze
				// certificate renewal for the process lifetime with no further signal.
				logger.LogWarning(ex, "TLS certificate renewal check failed; will retry on the next tick");
			}

			try
			{
				await Task.Delay(_tickInterval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private async Task CheckAndRenewAsync(CancellationToken ct)
	{
		string host = GatewayCertificateStore.HostFromPublicUrl(options.CurrentValue.PublicUrl);
		X509Certificate2 candidate = await certificateStore.GetOrCreateAsync(host, logger, ct).ConfigureAwait(false);

		X509Certificate2? previous = holder.Current;
		if (previous is not null && previous.Thumbprint == candidate.Thumbprint)
		{
			// Nothing due — GetOrCreateAsync just reloaded the same stored certificate.
			candidate.Dispose();
			return;
		}

		holder.Current = candidate;
		logger.LogInformation(
			"Serving TLS certificate {Action} (SHA-256 {Fingerprint}, expires {NotAfter:u})",
			previous is null ? "loaded" : "rotated ahead of expiry",
			GatewayCertificateStore.Fingerprint(candidate), candidate.NotAfter.ToUniversalTime());

		if (previous is not null)
			ScheduleDispose(previous);
	}

	/// <summary>
	///   Disposes <paramref name="previous" /> after <see cref="_disposeGracePeriod" /> rather than
	///   immediately, so a handshake already in flight that captured the old instance from the
	///   Kestrel selector before the swap finishes using it. Runs detached from the tick loop (its
	///   own lifetime, not the caller's) so one rotation's grace wait never delays the next tick.
	/// </summary>
	private void ScheduleDispose(X509Certificate2 previous)
	{
		_ = DisposeAfterGraceAsync(previous);
	}

	private async Task DisposeAfterGraceAsync(X509Certificate2 previous)
	{
		// Deliberately no CancellationToken here: on host shutdown the grace wait should still run
		// to completion in the background rather than dispose a certificate a lingering handshake
		// might still be reading right at the moment of shutdown.
		await Task.Delay(_disposeGracePeriod).ConfigureAwait(false);
		try
		{
			previous.Dispose();
		}
		catch (Exception ex)
		{
			logger.LogDebug(ex, "Disposing the previous TLS certificate after rotation failed");
		}
	}
}
