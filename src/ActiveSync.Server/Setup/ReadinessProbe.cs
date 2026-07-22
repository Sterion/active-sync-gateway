using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Real readiness (/readyz): cheap parallel probes of the state database plus every
///   configured backend role whose provider implements <see cref="IReadinessSource" />
///   (connectivity only, no credentials — 401 counts as reachable). Component names are
///   the role names, lowercased. Results are cached briefly so probes from an orchestrator
///   cannot hammer the backends. /healthz stays a trivial liveness 200 — a dead mail
///   server should drain traffic, not restart pods.
///   "configured" is reported but does NOT gate the verdict: a gateway awaiting its first
///   mail backend is a working gateway (the admin UI is how you configure one), it just
///   answers 503 on EAS and Autodiscover until a backend is assigned.
/// </summary>
public sealed class ReadinessProbe(
	ISyncDbContextFactory dbFactory,
	BackendRolesProvider rolesProvider,
	BackendProviderRegistry registry,
	ILogger<ReadinessProbe> logger)
{
	private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
	private readonly SemaphoreSlim _gate = new(1, 1);
	// Published via Volatile so the lock-free fast path below reads a consistent reference; the
	// Components dictionary is never mutated after publish, so sharing it is safe.
	private CacheEntry? _cached;

	private sealed record CacheEntry(DateTime AtUtc, Dictionary<string, bool> Components);

	public async Task<(bool Ready, Dictionary<string, bool> Components)> CheckAsync(CancellationToken ct)
	{
		// E16: serve a warm cache without touching the gate, so concurrent probes (an orchestrator
		// scraping /readyz, k8s liveness) are not serialized behind a single semaphore for a value
		// that is already computed. The gate is taken only to run — and single-flight — a refresh.
		if (TryReadFresh() is { } fast)
			return fast;

		await _gate.WaitAsync(ct);
		try
		{
			if (TryReadFresh() is { } cached)
				return cached; // another caller refreshed while we waited

			List<(string Name, Task<bool> Probe)> probes = [("database", ProbeDatabaseAsync(ct))];
			foreach ((BackendRole role, RoleAssignment assignment) in rolesProvider.Current.Assignments.OrderBy(a => a.Key))
				if (registry.GetFor(assignment.ProviderName, role) is IReadinessSource source)
					probes.Add(($"{role}".ToLowerInvariant(),
						ProbeRoleAsync(source, role, assignment, ct)));

			Dictionary<string, bool> components = new(StringComparer.Ordinal);
			// Informational only (see IsReady): an unconfigured gateway is ready to be
			// configured, and an orchestrator that never sees it healthy can never get there.
			components[ConfiguredComponent] = rolesProvider.Current.IsMailConfigured;
			foreach ((string name, Task<bool> probe) in probes)
				components[name] = await probe;

			Volatile.Write(ref _cached, new CacheEntry(DateTime.UtcNow, components));
			return (IsReady(components), components);
		}
		finally
		{
			_gate.Release();
		}
	}

	private (bool Ready, Dictionary<string, bool> Components)? TryReadFresh()
	{
		CacheEntry? snapshot = Volatile.Read(ref _cached);
		if (snapshot is { } cached && DateTime.UtcNow - cached.AtUtc < CacheTtl)
			return (IsReady(cached.Components), cached.Components);
		return null;
	}

	private const string ConfiguredComponent = "configured";

	/// <summary>Every probed component must pass; "configured" is reported, not required.</summary>
	private static bool IsReady(Dictionary<string, bool> components)
	{
		return components.All(c => c.Key == ConfiguredComponent || c.Value);
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

	private async Task<bool> ProbeRoleAsync(
		IReadinessSource source, BackendRole role, RoleAssignment assignment, CancellationToken ct)
	{
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(ProbeTimeout);
			return await source.ProbeReadinessAsync(assignment.Settings, timeout.Token);
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			logger.LogWarning("Readiness: {Role} probe ({Provider}) failed ({Reason})",
				role, assignment.ProviderName, ex.GetBaseException().Message);
			return false;
		}
	}
}
