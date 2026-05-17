using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class GroupsController : ControllerBase
{
    private readonly IClientGroupRepository _groupRepo;
    private readonly AuditLogger _audit;

    public GroupsController(IClientGroupRepository groupRepo, AuditLogger audit)
    {
        _groupRepo = groupRepo;
        _audit = audit;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<GroupResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        var groups = await _groupRepo.GetAllAsync(ct);
        return Ok(groups.Select(MapToResponse));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(GroupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var group = await _groupRepo.GetByIdAsync(id, ct);
        if (group is null)
            return NotFound();

        return Ok(MapToResponse(group));
    }

    [HttpPost]
    [ProducesResponseType(typeof(GroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateGroupRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Group name is required.");

        var group = new ClientGroup
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            DefaultImageId = request.DefaultImageId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _groupRepo.AddAsync(group, ct);
        await _audit.RecordAsync("group.create", true, "group", created.Id.ToString(),
            $"name={created.Name}", ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToResponse(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(GroupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateGroupRequest request,
        CancellationToken ct = default)
    {
        var group = await _groupRepo.GetByIdAsync(id, ct);
        if (group is null)
            return NotFound();

        if (request.Name is not null)
            group.Name = request.Name;
        if (request.Description is not null)
            group.Description = request.Description;
        if (request.DefaultImageId.HasValue)
            group.DefaultImageId = request.DefaultImageId;

        group.UpdatedAt = DateTime.UtcNow;
        await _groupRepo.UpdateAsync(group, ct);
        await _audit.RecordAsync("group.update", true, "group", group.Id.ToString(),
            $"name={group.Name}", ct);

        return Ok(MapToResponse(group));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var group = await _groupRepo.GetByIdAsync(id, ct);
        if (group is null)
            return NotFound();

        await _groupRepo.DeleteAsync(id, ct);
        await _audit.RecordAsync("group.delete", true, "group", id.ToString(),
            $"name={group.Name}", ct);
        return NoContent();
    }

    private static GroupResponse MapToResponse(ClientGroup g)
    {
        return new GroupResponse(
            Id: g.Id,
            Name: g.Name,
            Description: g.Description,
            DefaultImageId: g.DefaultImageId,
            DefaultImageName: g.DefaultImage?.Name,
            ClientCount: g.Clients.Count,
            CreatedAt: g.CreatedAt,
            UpdatedAt: g.UpdatedAt);
    }
}
