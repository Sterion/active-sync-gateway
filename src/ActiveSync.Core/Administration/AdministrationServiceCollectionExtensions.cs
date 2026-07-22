using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Core.Administration;

/// <summary>
///   Registers the shared admin services (<see cref="DeviceAdminService" />,
///   <see cref="ShareAdminService" />, <see cref="LogQueryService" />) — the single validated
///   read/write path over the device, share and log tables that both the web admin API and the
///   `eas` CLI consume (S3/C18). Each depends only on <c>ISyncDbContextFactory</c>, so it is a
///   singleton that opens short-lived contexts, exactly like <c>AccountStore</c>.
/// </summary>
public static class AdministrationServiceCollectionExtensions
{
	public static IServiceCollection AddAdministrationServices(this IServiceCollection services)
	{
		services.AddSingleton<DeviceAdminService>();
		services.AddSingleton<ShareAdminService>();
		services.AddSingleton<LogQueryService>();
		return services;
	}
}
