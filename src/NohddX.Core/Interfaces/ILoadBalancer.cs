using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface ILoadBalancer
{
    ClusterNode? SelectNode(ClientMachine client);
    Task RebalanceAsync(CancellationToken ct = default);
    void UpdateNodeMetrics(Guid nodeId, double cpu, double memory, double iops, int connections);
}
