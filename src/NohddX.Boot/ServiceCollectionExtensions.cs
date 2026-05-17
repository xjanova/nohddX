using Microsoft.Extensions.DependencyInjection;
using NohddX.Boot.DhcpProxy;
using NohddX.Boot.Discovery;
using NohddX.Boot.Http;
using NohddX.Boot.Tftp;
using NohddX.Core.Interfaces;

namespace NohddX.Boot;

/// <summary>
/// Extension methods for registering NohddX.Boot services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all NohddX Boot services: DHCP Proxy, TFTP server, iPXE script generator,
    /// and boot endpoint handler.
    /// </summary>
    public static IServiceCollection AddNohddxBoot(this IServiceCollection services)
    {
        // DHCP Proxy: registered as singleton so it can be resolved as both IDhcpProxyService
        // and as a hosted service (BackgroundService).
        services.AddSingleton<DhcpProxyService>();
        services.AddSingleton<IDhcpProxyService>(sp => sp.GetRequiredService<DhcpProxyService>());
        services.AddHostedService(sp => sp.GetRequiredService<DhcpProxyService>());

        // iPXE binary provisioner: runs BEFORE the TFTP server so that by the
        // time PXE clients show up, the TFTP root has the binaries they ask for.
        services.AddHostedService<IpxeBinaryProvisioner>();

        // TFTP Server: same pattern as DHCP proxy.
        services.AddSingleton<TftpServer>();
        services.AddSingleton<ITftpService>(sp => sp.GetRequiredService<TftpServer>());
        services.AddHostedService(sp => sp.GetRequiredService<TftpServer>());

        // iPXE script generator
        services.AddSingleton<IIpxeScriptGenerator, IpxeScriptGenerator>();

        // Boot endpoint handler (used by API controllers)
        services.AddScoped<BootEndpointHandler>();

        // UDP discovery responder: lets agents find the server with a broadcast probe.
        services.AddHostedService<DiscoveryResponderService>();

        return services;
    }
}
