using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IClusterService
{
    Task JoinClusterAsync(string nodeAddress, CancellationToken ct = default);
    Task LeaveClusterAsync(CancellationToken ct = default);
    ClusterNode? GetLeaderNode();
    IReadOnlyList<ClusterNode> GetClusterNodes();
    bool IsLeader { get; }
    event EventHandler<ClusterNode>? NodeJoined;
    event EventHandler<ClusterNode>? NodeLeft;
    event EventHandler<ClusterNode>? LeaderChanged;
}
