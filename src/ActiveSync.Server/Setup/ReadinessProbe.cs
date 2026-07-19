using ActiveSync.Core.Accounts;
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
	private (DateTime AtUtc, Dictionary<string, bool> Components)? _cached;

	public async Task<(bool Ready, Dictionary<string, bool> Components)> CheckAsync(CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			if (_cached is { } cached && DateTime.UtcNow - cached.AtUtc < CacheTtl)
				return (cached.Components.Values.All(v => v), cached.Components);

			List<(string Name, Task<bool> Probe)> probes = [("database", ProbeDatabaseAsync(ct))];
			foreach ((BackendRole role, RoleAssignment assignment) in rolesProvider.Current.Assignments.OrderBy(a => a.Key))
				if (registry.GetFor(assignment.ProviderName, role) is IReadinessSource source)
					probes.Add(($"{role}".ToLowerInvariant(),
						ProbeRoleAsync(source, role, assignment, ct)));

			Dictionary<string, bool> components = new(StringComparer.Ordinal);
			foreach ((string name, Task<bool> probe) in probes)
				components[name] = await probe;

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
