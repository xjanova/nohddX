using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.ClientMgmt.Services;

public class HardwareProfileManager
{
    private readonly IHardwareProfileRepository _profileRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ILogger<HardwareProfileManager> _logger;

    public HardwareProfileManager(
        IHardwareProfileRepository profileRepo,
        IClientRepository clientRepo,
        ILogger<HardwareProfileManager> logger)
    {
        _profileRepo = profileRepo;
        _clientRepo = clientRepo;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new hardware profile for a machine type.
    /// </summary>
    public async Task<HardwareProfile> CreateProfileAsync(
        string name,
        string? description = null,
        string? driverPackPath = null,
        BootMode bootMode = BootMode.Uefi,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var profile = new HardwareProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            DriverPackPath = driverPackPath,
            BootMode = bootMode,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _profileRepo.AddAsync(profile, ct);
        _logger.LogInformation("Created hardware profile '{Name}' ({Id})", name, created.Id);
        return created;
    }

    /// <summary>
    /// Updates an existing hardware profile.
    /// </summary>
    public async Task UpdateProfileAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? driverPackPath = null,
        BootMode? bootMode = null,
        CancellationToken ct = default)
    {
        var profile = await _profileRepo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"Hardware profile {id} not found.");

        if (name is not null) profile.Name = name;
        if (description is not null) profile.Description = description;
        if (driverPackPath is not null) profile.DriverPackPath = driverPackPath;
        if (bootMode.HasValue) profile.BootMode = bootMode.Value;
        profile.UpdatedAt = DateTime.UtcNow;

        await _profileRepo.UpdateAsync(profile, ct);
        _logger.LogInformation("Updated hardware profile '{Name}' ({Id})", profile.Name, id);
    }

    /// <summary>
    /// Deletes a hardware profile. Unlinks any clients using it first.
    /// </summary>
    public async Task DeleteProfileAsync(Guid id, CancellationToken ct = default)
    {
        var allClients = await _clientRepo.GetAllAsync(ct);
        var linkedClients = allClients.Where(c => c.HardwareProfileId == id).ToList();

        if (linkedClients.Count > 0)
        {
            _logger.LogWarning(
                "Unlinking {Count} client(s) from hardware profile {Id} before deletion",
                linkedClients.Count, id);

            foreach (var client in linkedClients)
            {
                client.HardwareProfileId = null;
                client.UpdatedAt = DateTime.UtcNow;
                await _clientRepo.UpdateAsync(client, ct);
            }
        }

        await _profileRepo.DeleteAsync(id, ct);
        _logger.LogInformation("Deleted hardware profile {Id}", id);
    }

    /// <summary>
    /// Retrieves all hardware profiles.
    /// </summary>
    public async Task<IReadOnlyList<HardwareProfile>> GetAllProfilesAsync(
        CancellationToken ct = default)
    {
        return await _profileRepo.GetAllAsync(ct);
    }

    /// <summary>
    /// Assigns a hardware profile to a client machine.
    /// </summary>
    public async Task AssignProfileToClientAsync(
        Guid clientId,
        Guid profileId,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByIdAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        _ = await _profileRepo.GetByIdAsync(profileId, ct)
            ?? throw new InvalidOperationException($"Hardware profile {profileId} not found.");

        client.HardwareProfileId = profileId;
        client.UpdatedAt = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);

        _logger.LogInformation("Assigned hardware profile {ProfileId} to client {ClientId}",
            profileId, clientId);
    }
}
