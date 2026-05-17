using Microsoft.Extensions.DependencyInjection;
using NohddX.Monitoring.Alerts;
using NohddX.Monitoring.Health;

namespace NohddX.Monitoring;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NohddX monitoring services including metrics collection,
    /// health checks, and alert management.
    /// </summary>
    public static IServiceCollection AddNohddxMonitoring(this IServiceCollection services)
    {
        services.AddSingleton<AlertManager>();
        services.AddSingleton<HealthCheckService>();
        services.AddHostedService<SystemHealthCollector>();

        return services;
    }
}
