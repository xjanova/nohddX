using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.ClientMgmt.Services;

public class GroupManager
{
    private readonly IClientGroupRepository _groupRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ILogger<GroupManager> _logger;

    public GroupManager(
        IClientGroupRepository groupRepo,
        IClientRepository clientRepo,
        ILogger<GroupManager> logger)
    {
        _groupRepo = groupRepo;
        _clientRepo = clientRepo;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new client group with an optional default boot image.
    /// </summary>
    public async Task<ClientGroup> CreateGroupAsync(
        string name,
        string? description = null,
        Guid? defaultImageId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var group = new ClientGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            DefaultImageId = defaultImageId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _groupRepo.AddAsync(group, ct);
        _logger.LogInformation("Created client group '{Name}' ({Id})", name, created.Id);
        return created;
    }

    /// <summary>
    /// Updates an existing client group's properties.
    /// </summary>
    public async Task UpdateGroupAsync(
        Guid id,
        string? name = null,
        string? description = null,
        Guid? defaultImageId = null,
        CancellationToken ct = default)
    {
        var group = await _groupRepo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"Group {id} not found.");

        if (name is not null) group.Name = name;
        if (description is not null) group.Description = description;
        if (defaultImageId is not null) group.DefaultImageId = defaultImageId;
        group.UpdatedAt = DateTime.UtcNow;

        await _groupRepo.UpdateAsync(group, ct);
        _logger.LogInformation("Updated client group '{Name}' ({Id})", group.Name, id);
    }

    /// <summary>
    /// Deletes a client group. Fails if clients are still assigned to it.
    /// </summary>
    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        var clients = await _clientRepo.GetByGroupAsync(id, ct);
        if (clients.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot delete group {id}: {clients.Count} client(s) still assigned. " +
                "Move or remove them first.");
        }

        await _groupRepo.DeleteAsync(id, ct);
        _logger.LogInformation("Deleted client group {Id}", id);
    }

    /// <summary>
    /// Retrieves all client groups.
    /// </summary>
    public async Task<IReadOnlyList<ClientGroup>> GetAllGroupsAsync(CancellationToken ct = default)
    {
        return await _groupRepo.GetAllAsync(ct);
    }

    /// <summary>
    /// Moves one or more clients to a target group.
    /// </summary>
    public async Task MoveClientsToGroupAsync(
        IReadOnlyList<Guid> clientIds,
        Guid targetGroupId,
        CancellationToken ct = default)
    {
        // Verify target group exists
        var targetGroup = await _groupRepo.GetByIdAsync(targetGroupId, ct)
            ?? throw new InvalidOperationException($"Target group {targetGroupId} not found.");

        foreach (var clientId in clientIds)
        {
            var client = await _clientRepo.GetByIdAsync(clientId, ct);
            if (client is null)
            {
                _logger.LogWarning("Client {Id} not found during group move, skipping", clientId);
                continue;
            }

            client.GroupId = targetGroupId;
            client.UpdatedAt = DateTime.UtcNow;
            await _clientRepo.UpdateAsync(client, ct);
        }

        _logger.LogInformation("Moved {Count} client(s) to group '{Group}' ({Id})",
            clientIds.Count, targetGroup.Name, targetGroupId);
    }
}
