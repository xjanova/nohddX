using Microsoft.Extensions.DependencyInjection;
using NohddX.Core.Interfaces;
using NohddX.Iscsi.Handlers;
using NohddX.Iscsi.Session;

namespace NohddX.Iscsi;

/// <summary>
/// Extension methods for registering iSCSI target services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNohddxIscsi(this IServiceCollection services)
    {
        services.AddSingleton<TargetRegistry>();
        services.AddSingleton<IscsiSessionManager>();
        services.AddSingleton<ScsiCommandHandler>();
        services.AddSingleton<IscsiTargetService>();
        services.AddSingleton<IIscsiTargetManager>(sp => sp.GetRequiredService<IscsiTargetService>());
        services.AddHostedService(sp => sp.GetRequiredService<IscsiTargetService>());
        return services;
    }
}
