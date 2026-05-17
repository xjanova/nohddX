using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Cluster.LoadBalancer;

public class WeightedLeastConnections : ILoadBalancer
{
    private readonly ConcurrentDictionary<Guid, NodeMetrics> _metrics = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeightedLeastConnections> _logger;

    private const double CpuWeight = 0.3;
    private const double MemoryWeight = 0.2;
    private const double ConnectionWeight = 0.4;
    private const double DiskIopsWeight = 0.1;
    private const double MaxExpectedIops = 10_000.0;

    public WeightedLeastConnections(
        IServiceScopeFactory scopeFactory,
        ILogger<WeightedLeastConnections> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ClusterNode? SelectNode(ClientMachine client)
    {
        var bestNode = (ClusterNode?)null;
        var bestWeight = double.MinValue;

        foreach (var (nodeId, metrics) in _metrics)
        {
            if (metrics.Status != NodeStatus.Online)
                continue;

            if (metrics.Node is null)
                continue;

            var maxConns = metrics.MaxConnections > 0 ? metrics.MaxConnections : 1;
            var maxIops = MaxExpectedIops;

            var cpuScore = (1.0 - Math.Clamp(metrics.CpuUsage / 100.0, 0, 1)) * CpuWeight;
            var memScore = (1.0 - Math.Clamp(metrics.MemoryUsage / 100.0, 0, 1)) * MemoryWeight;
            var connScore = (1.0 - Math.Clamp((double)metrics.ActiveConnections / maxConns, 0, 1)) * ConnectionWeight;
            var iopsScore = (1.0 - Math.Clamp(metrics.DiskIops / maxIops, 0, 1)) * DiskIopsWeight;

            var weight = cpuScore + memScore + connScore + iopsScore;

            if (weight > bestWeight)
            {
                bestWeight = weight;
                bestNode = metrics.Node;
            }
        }

        if (bestNode is not null)
        {
            _logger.LogInformation("Selected node {NodeId} for client {ClientId} (weight={Weight:F3})",
                bestNode.Id, client.Id, bestWeight);
        }
        else
        {
            _logger.LogWarning("No available node for client {ClientId}", client.Id);
        }

        return bestNode;
    }

    public async Task RebalanceAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting cluster rebalance check");

        using var scope = _scopeFactory.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<IClusterNodeRepository>();

        var onlineNodes = await nodeRepo.GetOnlineNodesAsync(ct);
        if (onlineNodes.Count == 0)
        {
            _logger.LogWarning("No online nodes available for rebalancing");
            return;
        }

        var totalClients = onlineNodes.Sum(n => n.CurrentClientCount);
        if (totalClients == 0) return;

        var avgClients = (double)totalClients / onlineNodes.Count;

        var overloaded = 0;
        var underloaded = 0;

        foreach (var node in onlineNodes)
        {
            var deviation = Math.Abs(node.CurrentClientCount - avgClients) / Math.Max(avgClients, 1) * 100;

            if (node.CurrentClientCount > avgClients && deviation > 20)
            {
                overloaded++;
                _logger.LogInformation("Node {Hostname} overloaded: {Count} clients (avg={Avg:F0})",
                    node.Hostname, node.CurrentClientCount, avgClients);
            }
            else if (node.CurrentClientCount < avgClients && deviation > 20)
            {
                underloaded++;
            }
        }

        if (overloaded > 0 && underloaded > 0)
            _logger.LogInformation("Rebalance recommended: {O} overloaded, {U} underloaded", overloaded, underloaded);
        else
            _logger.LogInformation("Cluster is balanced");
    }

    public void UpdateNodeMetrics(Guid nodeId, double cpu, double memory, double iops, int connections)
    {
        _metrics.AddOrUpdate(nodeId,
            _ => new NodeMetrics
            {
                CpuUsage = cpu, MemoryUsage = memory, DiskIops = iops,
                ActiveConnections = connections, MaxConnections = 2000,
                Status = NodeStatus.Online, LastUpdate = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.CpuUsage = cpu;
                existing.MemoryUsage = memory;
                existing.DiskIops = iops;
                existing.ActiveConnections = connections;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
    }

    public void SetNodeReference(Guid nodeId, ClusterNode node)
    {
        _metrics.AddOrUpdate(nodeId,
            _ => new NodeMetrics
            {
                Node = node, Status = node.Status,
                MaxConnections = node.MaxClients > 0 ? node.MaxClients : 2000,
                LastUpdate = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.Node = node;
                existing.Status = node.Status;
                if (node.MaxClients > 0) existing.MaxConnections = node.MaxClients;
                return existing;
            });
    }

    public void MarkNodeOffline(Guid nodeId)
    {
        if (_metrics.TryGetValue(nodeId, out var metrics))
            metrics.Status = NodeStatus.Offline;
    }

    private class NodeMetrics
    {
        public ClusterNode? Node { get; set; }
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double DiskIops { get; set; }
        public int ActiveConnections { get; set; }
        public int MaxConnections { get; set; }
        public NodeStatus Status { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}
