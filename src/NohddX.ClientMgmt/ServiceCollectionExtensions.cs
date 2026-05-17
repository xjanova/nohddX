using Microsoft.Extensions.DependencyInjection;
using NohddX.ClientMgmt.Services;
using NohddX.ClientMgmt.WakeOnLan;

namespace NohddX.ClientMgmt;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NohddX client management services.
    /// </summary>
    public static IServiceCollection AddNohddxClientManagement(this IServiceCollection services)
    {
        services.AddScoped<ClientManager>();
        services.AddScoped<GroupManager>();
        services.AddScoped<HardwareProfileManager>();
        services.AddSingleton<WolService>();
        services.AddHostedService<IscsiTargetBootstrap>();

        return services;
    }
}
