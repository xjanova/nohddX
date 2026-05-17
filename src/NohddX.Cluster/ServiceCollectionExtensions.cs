using Microsoft.Extensions.DependencyInjection;
using NohddX.Cluster.Consensus;
using NohddX.Cluster.Discovery;
using NohddX.Cluster.Failover;
using NohddX.Cluster.Heartbeat;
using NohddX.Cluster.LoadBalancer;
using NohddX.Cluster.Sync;
using NohddX.Core.Interfaces;

namespace NohddX.Cluster;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNohddxCluster(this IServiceCollection services)
    {
        // Discovery & heartbeat infrastructure
        services.AddSingleton<NodeDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeDiscoveryService>());
        services.AddSingleton<HeartbeatService>();

        // Raft consensus / cluster membership
        services.AddSingleton<RaftClusterService>();
        services.AddSingleton<IClusterService>(sp => sp.GetRequiredService<RaftClusterService>());
        services.AddHostedService(sp => sp.GetRequiredService<RaftClusterService>());

        // Load balancing
        services.AddSingleton<ILoadBalancer, WeightedLeastConnections>();

        // Failover
        services.AddSingleton<IFailoverManager, FailoverManager>();

        // Image synchronization
        services.AddSingleton<ImageSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<ImageSyncService>());

        return services;
    }
}
