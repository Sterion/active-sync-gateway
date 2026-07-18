using System.Net.Sockets;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Real readiness (/readyz): cheap parallel probes of the state database, the IMAP
///   listener (TCP connect, no credentials) and the configured DAV base URLs (any HTTP
///   answer — including 401 — counts as reachable). Results are cached briefly so probes
///   from an orchestrator cannot hammer the backends. /healthz stays a trivial liveness
///   200 — a dead IMAP server should drain traffic, not restart pods.
/// </summary>
public sealed class ReadinessProbe(
	ISyncDbContextFactory dbFactory,
	IOptions<ActiveSyncOptions> options,
	ILogger<ReadinessProbe> logger)
{
	private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private (DateTime AtUtc, Dictionary<string, bool> Components)? _cached;

	public async Task<(bool Ready, Dictionary<string, bool> Components)> CheckAsync(CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			if (_cached is { } cached && DateTime.UtcNow - cached.AtUtc < CacheTtl)
				return (cached.Components.Values.All(v => v), cached.Components);

			ActiveSyncOptions config = options.Value;
			Task<bool> database = ProbeDatabaseAsync(ct);
			Task<bool> imap = ProbeTcpAsync(config.Imap.Host, config.Imap.Port, ct);
			Task<bool>? calDav = config.CalDav is { } cal ? ProbeHttpAsync(cal.BaseUrl, ct) : null;
			Task<bool>? cardDav = config.CardDav is { } card ? ProbeHttpAsync(card.BaseUrl, ct) : null;

			Dictionary<string, bool> components = new(StringComparer.Ordinal)
			{
				["database"] = await database,
				["imap"] = await imap
			};
			if (calDav is not null)
				components["caldav"] = await calDav;
			if (cardDav is not null)
				components["carddav"] = await cardDav;

			_cached = (DateTime.UtcNow, components);
			return (components.Values.All(v => v), components);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<bool> ProbeDatabaseAsync(CancellationToken ct)
	{
		try
		{
			await using SyncDbContext db = dbFactory.CreateDbContext();
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(ProbeTimeout);
			await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			logger.LogWarning("Readiness: database probe failed ({Reason})", ex.GetBaseException().Message);
			return false;
		}
	}

	private async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken ct)
	{
		try
		{
			using TcpClient client = new();
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(ProbeTimeout);
			await client.ConnectAsync(host, port, timeout.Token);
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			logger.LogWarning("Readiness: IMAP probe {Host}:{Port} failed ({Reason})",
				host, port, ex.GetBaseException().Message);
			return false;
		}
	}

	private async Task<bool> ProbeHttpAsync(string baseUrl, CancellationToken ct)
	{
		try
		{
			using HttpClient http = new(new SocketsHttpHandler
			{
				// Reachability only — DAV endpoints legitimately answer 401 without creds,
				// and lab deployments use self-signed certificates the gateway is
				// separately configured to trust.
				SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
			});
			http.Timeout = ProbeTimeout;
			using HttpRequestMessage request = new(HttpMethod.Options, baseUrl);
			using HttpResponseMessage response = await http.SendAsync(request, ct);
			return true; // any HTTP status = the server answered
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			logger.LogWarning("Readiness: DAV probe {BaseUrl} failed ({Reason})",
				baseUrl, ex.GetBaseException().Message);
			return false;
		}
	}
}
