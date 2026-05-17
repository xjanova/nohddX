using NohddX.Core.Models;

namespace NohddX.Api.DTOs;

public record JoinClusterRequest(
    string NodeAddress);

public record ClusterStatusResponse(
    bool IsCluster,
    int NodeCount,
    string? LeaderNode,
    int TotalClients,
    IReadOnlyList<ClusterNodeResponse> Nodes);

public record ClusterNodeResponse(
    Guid Id,
    string Hostname,
    string IpAddress,
    int Port,
    ClusterRole Role,
    NodeStatus Status,
    int MaxClients,
    int CurrentClientCount,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    double DiskIops,
    double NetworkBandwidthMbps,
    DateTime? LastHeartbeat);
