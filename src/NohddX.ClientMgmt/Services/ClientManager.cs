using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.ClientMgmt.Services;

public class ClientManager
{
    private readonly IClientRepository _clientRepo;
    private readonly IBootAssignmentRepository _assignmentRepo;
    private readonly IClientGroupRepository _groupRepo;
    private readonly IImageRepository _imageRepo;
    private readonly ICowStorageEngine _cowStorage;
    private readonly IIscsiTargetManager _iscsi;
    private readonly ILogger<ClientManager> _logger;

    public ClientManager(
        IClientRepository clientRepo,
        IBootAssignmentRepository assignmentRepo,
        IClientGroupRepository groupRepo,
        IImageRepository imageRepo,
        ICowStorageEngine cowStorage,
        IIscsiTargetManager iscsi,
        ILogger<ClientManager> logger)
    {
        _clientRepo = clientRepo;
        _assignmentRepo = assignmentRepo;
        _groupRepo = groupRepo;
        _imageRepo = imageRepo;
        _cowStorage = cowStorage;
        _iscsi = iscsi;
        _logger = logger;
    }

    /// <summary>
    /// Registers a new client or updates an existing one identified by MAC address.
    /// </summary>
    public async Task<ClientMachine> RegisterClientAsync(
        string macAddress,
        string? hostname = null,
        Guid? groupId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        var normalized = NormalizeMac(macAddress);

        var existing = await _clientRepo.GetByMacAddressAsync(normalized, ct);
        if (existing is not null)
        {
            existing.Hostname = hostname ?? existing.Hostname;
            existing.GroupId = groupId ?? existing.GroupId;
            existing.Status = ClientStatus.Online;
            existing.LastSeen = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
            await _clientRepo.UpdateAsync(existing, ct);
            _logger.LogInformation("Updated existing client {Mac} ({Id})", normalized, existing.Id);
            return existing;
        }

        var client = new ClientMachine
        {
            Id = Guid.NewGuid(),
            MacAddress = normalized,
            Hostname = hostname,
            GroupId = groupId,
            Status = ClientStatus.Online,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _clientRepo.AddAsync(client, ct);
        _logger.LogInformation("Registered new client {Mac} ({Id})", normalized, created.Id);
        return created;
    }

    /// <summary>
    /// Removes a client registration entirely.
    /// </summary>
    public async Task UnregisterClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            _logger.LogWarning("Attempted to unregister non-existent client {Id}", clientId);
            return;
        }

        // Remove any boot assignment first
        var assignment = await _assignmentRepo.GetByClientIdAsync(clientId, ct);
        if (assignment is not null)
        {
            await _assignmentRepo.DeleteAsync(assignment.Id, ct);
        }

        // Drop the in-memory iSCSI target so future logins for this client
        // are rejected instead of serving stale data.
        await _iscsi.UnregisterTargetAsync(clientId.ToString(), ct);

        await _clientRepo.DeleteAsync(clientId, ct);
        _logger.LogInformation("Unregistered client {Mac} ({Id})", client.MacAddress, clientId);
    }

    /// <summary>
    /// Assigns a boot image to a client, creating or updating the boot assignment.
    /// </summary>
    public async Task<BootAssignment> AssignImageAsync(
        Guid clientId,
        Guid imageId,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        var existing = await _assignmentRepo.GetByClientIdAsync(clientId, ct);
        if (existing is not null)
        {
            existing.ImageId = imageId;
            existing.HardwareProfileId = profileId ?? existing.HardwareProfileId;
            existing.IsActive = true;
            existing.UpdatedAt = DateTime.UtcNow;
            await _assignmentRepo.UpdateAsync(existing, ct);
            await RegisterIscsiTargetAsync(clientId, imageId, ct);
            _logger.LogInformation("Updated image assignment for client {Mac}: image {ImageId}",
                client.MacAddress, imageId);
            return existing;
        }

        var assignment = new BootAssignment
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ImageId = imageId,
            HardwareProfileId = profileId,
            Priority = 0,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _assignmentRepo.AddAsync(assignment, ct);
        await RegisterIscsiTargetAsync(clientId, imageId, ct);
        _logger.LogInformation("Assigned image {ImageId} to client {Mac}",
            imageId, client.MacAddress);
        return created;
    }

    /// <summary>
    /// Adds the per-client iSCSI target to the in-memory registry. Without this,
    /// the iPXE-driven login from the booting client hits the registry, finds
    /// no match for the per-client IQN, and rejects the login with "target not
    /// found" — so the client can never boot.
    /// </summary>
    private async Task RegisterIscsiTargetAsync(Guid clientId, Guid imageId, CancellationToken ct)
    {
        var image = await _imageRepo.GetByIdAsync(imageId, ct);
        if (image is null || string.IsNullOrWhiteSpace(image.FilePath))
        {
            _logger.LogWarning(
                "Cannot register iSCSI target for client {ClientId}: image {ImageId} not found or has no FilePath",
                clientId, imageId);
            return;
        }

        await _iscsi.RegisterTargetAsync(clientId.ToString(), image.FilePath, ct);
    }

    /// <summary>
    /// Removes the boot image assignment from a client.
    /// </summary>
    public async Task UnassignImageAsync(Guid clientId, CancellationToken ct = default)
    {
        var assignment = await _assignmentRepo.GetByClientIdAsync(clientId, ct);
        if (assignment is null)
        {
            _logger.LogWarning("No assignment found for client {Id} to remove", clientId);
            return;
        }

        await _assignmentRepo.DeleteAsync(assignment.Id, ct);
        await _iscsi.UnregisterTargetAsync(clientId.ToString(), ct);
        _logger.LogInformation("Removed image assignment from client {Id}", clientId);
    }

    /// <summary>
    /// Updates the status of a client (e.g. Online, Offline, Booting, Error).
    /// </summary>
    public async Task UpdateStatusAsync(
        Guid clientId,
        ClientStatus status,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        client.Status = status;
        client.LastSeen = DateTime.UtcNow;
        client.UpdatedAt = DateTime.UtcNow;

        if (status == ClientStatus.Online)
        {
            client.LastBootTime = DateTime.UtcNow;
        }

        await _clientRepo.UpdateAsync(client, ct);
        _logger.LogDebug("Client {Id} status updated to {Status}", clientId, status);
    }

    /// <summary>
    /// Retrieves a client by its MAC address.
    /// </summary>
    public async Task<ClientMachine?> GetClientByMacAsync(
        string macAddress,
        CancellationToken ct = default)
    {
        return await _clientRepo.GetByMacAddressAsync(NormalizeMac(macAddress), ct);
    }

    /// <summary>
    /// Retrieves all clients belonging to a specific group.
    /// </summary>
    public async Task<IReadOnlyList<ClientMachine>> GetClientsByGroupAsync(
        Guid groupId,
        CancellationToken ct = default)
    {
        return await _clientRepo.GetByGroupAsync(groupId, ct);
    }

    /// <summary>
    /// Retrieves all registered clients.
    /// </summary>
    public async Task<IReadOnlyList<ClientMachine>> GetAllClientsAsync(CancellationToken ct = default)
    {
        return await _clientRepo.GetAllAsync(ct);
    }

    /// <summary>
    /// Resets the COW overlay for a client, discarding all write-layer changes.
    /// </summary>
    public async Task ResetClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        await _cowStorage.ResetOverlayAsync(clientId.ToString(), ct);

        client.Status = ClientStatus.Offline;
        client.UpdatedAt = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);

        _logger.LogInformation("Reset overlay for client {Mac} ({Id})", client.MacAddress, clientId);
    }

    /// <summary>
    /// Assigns the same boot image to multiple clients at once.
    /// </summary>
    public async Task BulkAssignImageAsync(
        IReadOnlyList<Guid> clientIds,
        Guid imageId,
        CancellationToken ct = default)
    {
        foreach (var clientId in clientIds)
        {
            await AssignImageAsync(clientId, imageId, ct: ct);
        }

        _logger.LogInformation("Bulk-assigned image {ImageId} to {Count} clients",
            imageId, clientIds.Count);
    }

    /// <summary>
    /// Automatically registers a client with a default group when auto-registration is enabled.
    /// Looks for a group named "Default" or creates one if needed.
    /// </summary>
    public async Task<ClientMachine> AutoRegisterClientAsync(
        string macAddress,
        CancellationToken ct = default)
    {
        var normalized = NormalizeMac(macAddress);

        // Try to find a default group
        var groups = await _groupRepo.GetAllAsync(ct);
        var defaultGroup = groups.FirstOrDefault(g =>
            g.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation("Auto-registering client {Mac}", normalized);
        return await RegisterClientAsync(normalized, groupId: defaultGroup?.Id, ct: ct);
    }

    internal static string NormalizeMac(string mac)
    {
        // Canonical MAC format across the system is uppercase hyphen-separated:
        // AA-BB-CC-DD-EE-FF. This matches what iPXE substitutes for
        // ${mac:hexhyp}, the format used by BootEndpointHandler, and what
        // BootEndpointHandlerTests assert. Storing the same shape in the DB
        // means /api/boot/{mac}.ipxe lookups hit.
        var cleaned = mac.Replace("-", "").Replace(":", "").Replace(".", "").ToUpperInvariant();
        if (cleaned.Length != 12)
        {
            throw new ArgumentException($"Invalid MAC address: {mac}");
        }

        return string.Join("-",
            Enumerable.Range(0, 6).Select(i => cleaned.Substring(i * 2, 2)));
    }
}
