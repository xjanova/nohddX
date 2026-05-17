using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Api.Hubs;
using NohddX.ClientMgmt.Services;
using NohddX.ClientMgmt.WakeOnLan;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class ClientsController : ControllerBase
{
    private readonly IClientRepository _clientRepo;
    private readonly ClientManager _clientManager;
    private readonly WolService _wolService;
    private readonly DashboardNotifier _notifier;
    private readonly AuditLogger _audit;

    public ClientsController(
        IClientRepository clientRepo,
        ClientManager clientManager,
        WolService wolService,
        DashboardNotifier notifier,
        AuditLogger audit)
    {
        _clientRepo = clientRepo;
        _clientManager = clientManager;
        _wolService = wolService;
        _notifier = notifier;
        _audit = audit;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ClientResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var clients = await _clientRepo.GetAllAsync(ct);
        var paged = clients.Skip(skip).Take(take);
        return Ok(paged.Select(MapToResponse));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        return Ok(MapToResponse(client));
    }

    [HttpGet("mac/{mac}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByMac(string mac, CancellationToken ct = default)
    {
        var client = await _clientManager.GetClientByMacAsync(mac, ct);
        if (client is null)
            return NotFound();

        return Ok(MapToResponse(client));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        [FromBody] CreateClientRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.MacAddress))
            return BadRequest("MAC address is required.");

        var client = await _clientManager.RegisterClientAsync(
            request.MacAddress, request.Hostname, request.GroupId, ct);

        await _notifier.NotifyClientStatusChangedAsync(client.Id, client.Status.ToString());
        await _audit.RecordAsync("client.create", true, "client", client.Id.ToString(),
            $"mac={client.MacAddress} host={client.Hostname}", ct);

        return CreatedAtAction(nameof(GetById), new { id = client.Id }, MapToResponse(client));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateClientRequest request,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        if (request.Hostname is not null)
            client.Hostname = request.Hostname;
        if (request.GroupId.HasValue)
            client.GroupId = request.GroupId;
        if (request.HardwareProfileId.HasValue)
            client.HardwareProfileId = request.HardwareProfileId;

        client.UpdatedAt = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);

        return Ok(MapToResponse(client));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        await _clientManager.UnregisterClientAsync(id, ct);
        await _audit.RecordAsync("client.delete", true, "client", id.ToString(), null, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/wake")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Wake(Guid id, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        await _wolService.WakeAsync(client.MacAddress, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reset(Guid id, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        await _clientManager.ResetClientAsync(id, ct);
        await _notifier.NotifyClientStatusChangedAsync(id, ClientStatus.Offline.ToString());
        return NoContent();
    }

    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssignImage(
        Guid id,
        [FromBody] AssignImageRequest request,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(id, ct);
        if (client is null)
            return NotFound();

        var assignment = await _clientManager.AssignImageAsync(
            id, request.ImageId, request.ProfileId, ct);

        await _audit.RecordAsync("client.assign", true, "client", id.ToString(),
            $"imageId={request.ImageId}", ct);

        return Ok(assignment);
    }

    [HttpPost("bulk-assign")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkAssign(
        [FromBody] BulkAssignRequest request,
        CancellationToken ct = default)
    {
        if (request.ClientIds is null || request.ClientIds.Length == 0)
            return BadRequest("At least one client ID is required.");

        await _clientManager.BulkAssignImageAsync(request.ClientIds, request.ImageId, ct);
        await _audit.RecordAsync("client.bulk-assign", true, "image", request.ImageId.ToString(),
            $"count={request.ClientIds.Length}", ct);
        return NoContent();
    }

    [HttpPost("bulk-wake")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkWake(
        [FromBody] BulkWakeRequest request,
        CancellationToken ct = default)
    {
        if (request.ClientIds is null || request.ClientIds.Length == 0)
            return BadRequest("At least one client ID is required.");

        var macAddresses = new List<string>();
        foreach (var clientId in request.ClientIds)
        {
            var client = await _clientRepo.GetByIdAsync(clientId, ct);
            if (client is not null)
                macAddresses.Add(client.MacAddress);
        }

        await _wolService.WakeMultipleAsync(macAddresses, ct);
        return NoContent();
    }

    private static ClientResponse MapToResponse(ClientMachine c)
    {
        return new ClientResponse(
            Id: c.Id,
            MacAddress: c.MacAddress,
            Hostname: c.Hostname,
            IpAddress: c.IpAddress,
            Status: c.Status.ToString(),
            GroupName: c.Group?.Name,
            GroupId: c.GroupId,
            ImageName: c.BootAssignment?.Image?.Name,
            ImageId: c.BootAssignment?.ImageId,
            HardwareProfileName: c.HardwareProfile?.Name,
            HardwareProfileId: c.HardwareProfileId,
            LastSeen: c.LastSeen,
            LastBootTime: c.LastBootTime,
            CreatedAt: c.CreatedAt);
    }
}
