using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Api.Hubs;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class ClusterController : ControllerBase
{
    private readonly IClusterService _clusterService;
    private readonly IClusterNodeRepository _nodeRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ILoadBalancer _loadBalancer;
    private readonly DashboardNotifier _notifier;
    private readonly AuditLogger _audit;

    public ClusterController(
        IClusterService clusterService,
        IClusterNodeRepository nodeRepo,
        IClientRepository clientRepo,
        ILoadBalancer loadBalancer,
        DashboardNotifier notifier,
        AuditLogger audit)
    {
        _clusterService = clusterService;
        _nodeRepo = nodeRepo;
        _clientRepo = clientRepo;
        _loadBalancer = loadBalancer;
        _notifier = notifier;
        _audit = audit;
    }

    [HttpGet("nodes")]
    [ProducesResponseType(typeof(IEnumerable<ClusterNodeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNodes(CancellationToken ct = default)
    {
        var nodes = await _nodeRepo.GetAllAsync(ct);
        return Ok(nodes.Select(MapNodeToResponse));
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(ClusterStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default)
    {
        var nodes = _clusterService.GetClusterNodes();
        var leader = _clusterService.GetLeaderNode();
        var clientCount = await _clientRepo.GetCountAsync(ct);

        var response = new ClusterStatusResponse(
            IsCluster: nodes.Count > 1,
            NodeCount: nodes.Count,
            LeaderNode: leader?.Hostname,
            TotalClients: clientCount,
            Nodes: nodes.Select(MapNodeToResponse).ToList());

        return Ok(response);
    }

    [HttpPost("join")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Join(
        [FromBody] JoinClusterRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.NodeAddress))
            return BadRequest("Node address is required.");

        await _clusterService.JoinClusterAsync(request.NodeAddress, ct);
        await _notifier.NotifyClusterStateChangedAsync();
        await _audit.RecordAsync("cluster.join", true, "cluster", null,
            $"nodeAddress={request.NodeAddress}", ct);
        return NoContent();
    }

    [HttpPost("leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Leave(CancellationToken ct = default)
    {
        await _clusterService.LeaveClusterAsync(ct);
        await _notifier.NotifyClusterStateChangedAsync();
        await _audit.RecordAsync("cluster.leave", true, "cluster", null, null, ct);
        return NoContent();
    }

    [HttpPost("rebalance")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Rebalance(CancellationToken ct = default)
    {
        await _loadBalancer.RebalanceAsync(ct);
        await _notifier.NotifyClusterStateChangedAsync();
        await _audit.RecordAsync("cluster.rebalance", true, "cluster", null, null, ct);
        return NoContent();
    }

    private static ClusterNodeResponse MapNodeToResponse(ClusterNode n)
    {
        return new ClusterNodeResponse(
            Id: n.Id,
            Hostname: n.Hostname,
            IpAddress: n.IpAddress,
            Port: n.Port,
            Role: n.Role,
            Status: n.Status,
            MaxClients: n.MaxClients,
            CurrentClientCount: n.CurrentClientCount,
            CpuUsagePercent: n.CpuUsagePercent,
            MemoryUsagePercent: n.MemoryUsagePercent,
            DiskIops: n.DiskIops,
            NetworkBandwidthMbps: n.NetworkBandwidthMbps,
            LastHeartbeat: n.LastHeartbeat);
    }
}
