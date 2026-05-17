using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Api.Hubs;
using NohddX.ClientMgmt.Services;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

/// <summary>
/// Endpoints consumed by the bootstrap agent (NohddX.Agent) running on
/// freshly-booted client machines. The agent posts hardware info when
/// it comes up, polls the server for install instructions, and reports
/// progress while writing/serving an OS image.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly IClientRepository _clientRepo;
    private readonly IBootAssignmentRepository _assignmentRepo;
    private readonly IImageRepository _imageRepo;
    private readonly IBootEventRepository _eventRepo;
    private readonly ClientManager _clientManager;
    private readonly DashboardNotifier _notifier;
    private readonly AgentTokenService _tokens;
    private readonly AuditLogger _audit;
    private readonly SecurityOptions _security;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(
        IClientRepository clientRepo,
        IBootAssignmentRepository assignmentRepo,
        IImageRepository imageRepo,
        IBootEventRepository eventRepo,
        ClientManager clientManager,
        DashboardNotifier notifier,
        AgentTokenService tokens,
        AuditLogger audit,
        IOptions<SecurityOptions> security,
        ILogger<AgentsController> logger)
    {
        _clientRepo = clientRepo;
        _assignmentRepo = assignmentRepo;
        _imageRepo = imageRepo;
        _eventRepo = eventRepo;
        _clientManager = clientManager;
        _notifier = notifier;
        _tokens = tokens;
        _audit = audit;
        _security = security.Value;
        _logger = logger;
    }

    /// <summary>Liveness probe used by the agent's discovery routine.</summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    [Produces("text/plain")]
    public IActionResult Ping() => Content("pong", "text/plain");

    /// <summary>
    /// Registers (or re-registers) an agent with the hardware snapshot it
    /// just collected on the client. The first network interface's MAC is
    /// used as the stable client identity.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.AgentRegisterRateLimitPolicy)]
    [RequestSizeLimit(512 * 1024)]
    [ProducesResponseType(typeof(AgentRegisterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Register(
        [FromBody] AgentRegisterRequest request,
        CancellationToken ct = default)
    {
        if (request is null)
            return BadRequest("Body is required.");

        // Bootstrap-token gate (if configured)
        if (!string.IsNullOrEmpty(_security.BootstrapToken))
        {
            var provided = Request.Headers["X-Bootstrap-Token"].ToString();
            if (string.IsNullOrEmpty(provided) ||
                !FixedTimeEquals(provided, _security.BootstrapToken))
            {
                await _audit.RecordAsync("agent.register", false, "agent", null,
                    "missing or bad bootstrap token", ct);
                return Unauthorized("Bootstrap token required to register a new agent.");
            }
        }

        var primaryMac = request.Networks?
            .Where(n => !string.IsNullOrWhiteSpace(n.MacAddress))
            .OrderByDescending(n => n.IsConnected)
            .ThenByDescending(n => n.SpeedMbps)
            .Select(n => n.MacAddress)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(primaryMac))
            return BadRequest("At least one network interface with a MAC address is required.");

        var hostname = string.IsNullOrWhiteSpace(request.Hostname) ? null : request.Hostname;

        var client = await _clientManager.RegisterClientAsync(primaryMac, hostname, groupId: null, ct);

        var primaryIp = request.Networks?
            .Where(n => n.IsConnected && !string.IsNullOrWhiteSpace(n.IpAddress))
            .Select(n => n.IpAddress)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(primaryIp) && primaryIp != client.IpAddress)
        {
            client.IpAddress = primaryIp;
            client.UpdatedAt = DateTime.UtcNow;
            await _clientRepo.UpdateAsync(client, ct);
        }

        await _eventRepo.AddAsync(new BootEvent
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            EventType = "agent-register",
            Message = $"Agent registered from {primaryIp ?? "unknown ip"}",
            Success = true,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        }, ct);

        await _notifier.NotifyClientStatusChangedAsync(client.Id, client.Status.ToString());

        var token = _tokens.IssueToken(client.Id);

        await _audit.RecordAsync("agent.register", true, "agent", client.Id.ToString(),
            $"mac={primaryMac} ip={primaryIp}", ct);

        _logger.LogInformation(
            "Agent registered: {Hostname} {Mac} ({Id}) from {Ip}",
            hostname ?? "<no-hostname>", primaryMac, client.Id, primaryIp ?? "<no-ip>");

        return Ok(new AgentRegisterResponse(
            AgentId: client.Id.ToString(),
            Message: $"Welcome {hostname ?? primaryMac}",
            Token: token,
            TokenExpiryHours: _security.AgentTokenLifetimeHours));
    }

    /// <summary>
    /// Receives a status / progress update from the agent. Used to surface
    /// live progress in the operator dashboard.
    /// </summary>
    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = NohddxAuthSchemes.AgentPolicy)]
    [EnableRateLimiting(ServiceCollectionExtensions.AgentRateLimitPolicy)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Status(
        Guid id,
        [FromBody] AgentStatusUpdate update,
        CancellationToken ct = default)
    {
        if (!AgentSelfAuthorization.CallerIsSelfOrAdmin(HttpContext, id))
            return Forbid();

        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null) return NotFound();

        var newStatus = MapAgentStatusToClientStatus(update.Status, client.Status);
        var changed = newStatus != client.Status;

        client.Status = newStatus;
        client.LastSeen = DateTime.UtcNow;
        client.UpdatedAt = DateTime.UtcNow;
        if (newStatus == ClientStatus.Online)
            client.LastBootTime = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);

        if (changed)
            await _notifier.NotifyClientStatusChangedAsync(client.Id, newStatus.ToString());

        await _eventRepo.AddAsync(new BootEvent
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            EventType = $"agent-{update.Status}",
            Message = $"progress={update.Progress:0.##}%",
            Success = !update.Status.Contains("fail", StringComparison.OrdinalIgnoreCase),
            StartedAt = update.Timestamp == default ? DateTime.UtcNow : update.Timestamp,
            CompletedAt = DateTime.UtcNow
        }, ct);

        _logger.LogInformation(
            "Agent {Id} status: {Status} ({Progress:0.##}%)",
            id, update.Status, update.Progress);

        return NoContent();
    }

    /// <summary>
    /// Returns instructions for the install mode the operator-or-default
    /// has assigned to this client. Persistent install gets a download URL
    /// to a backing image; diskless/network boot gets metadata pointing at
    /// the iSCSI/iPXE entry point on the server.
    /// </summary>
    [HttpPost("{id:guid}/install")]
    [Authorize(Policy = NohddxAuthSchemes.AgentPolicy)]
    [EnableRateLimiting(ServiceCollectionExtensions.AgentRateLimitPolicy)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(typeof(AgentInstallInstructions), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Install(
        Guid id,
        [FromBody] AgentInstallRequest request,
        CancellationToken ct = default)
    {
        if (!AgentSelfAuthorization.CallerIsSelfOrAdmin(HttpContext, id))
            return Forbid();

        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null) return NotFound();

        var assignment = await _assignmentRepo.GetByClientIdAsync(id, ct);
        if (assignment is null || !assignment.IsActive)
            return Conflict("No active boot assignment. Assign an image to this client first.");

        var image = await _imageRepo.GetByIdAsync(assignment.ImageId, ct);
        if (image is null) return NotFound("Assigned image no longer exists.");

        var serverBase = $"{Request.Scheme}://{Request.Host}";
        var imageUrl = $"{serverBase}/api/images/{image.Id}/download";
        var bootUrl = $"{serverBase}/api/boot/{client.MacAddress}.ipxe";

        var primaryDisk = "/dev/sda";

        var metadata = new Dictionary<string, string>
        {
            ["imageId"] = image.Id.ToString(),
            ["imageName"] = image.Name,
            ["osType"] = image.OsType.ToString(),
            ["targetMac"] = client.MacAddress,
            ["bootScriptUrl"] = bootUrl
        };

        var instructions = new AgentInstallInstructions(
            ImageUrl: request.Mode.Equals("Persistent", StringComparison.OrdinalIgnoreCase) ? imageUrl : bootUrl,
            ImageSize: image.SizeBytes,
            TargetDisk: primaryDisk,
            PartitionScheme: "gpt",
            Metadata: metadata);

        _logger.LogInformation(
            "Sending {Mode} install instructions to agent {Id}: image={Image}",
            request.Mode, id, image.Name);

        return Ok(instructions);
    }

    private static ClientStatus MapAgentStatusToClientStatus(string agentStatus, ClientStatus current)
    {
        if (string.IsNullOrWhiteSpace(agentStatus)) return current;

        var s = agentStatus.ToLowerInvariant();
        if (s.Contains("fail") || s.Contains("error")) return ClientStatus.Error;
        if (s.Contains("complete")) return ClientStatus.Online;
        if (s.Contains("ready") || s.Contains("connected") || s.Contains("mounted")) return ClientStatus.Online;
        if (s.Contains("init") || s.Contains("requesting") || s.Contains("writing") || s.Contains("rebooting"))
            return ClientStatus.Booting;

        return current;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
