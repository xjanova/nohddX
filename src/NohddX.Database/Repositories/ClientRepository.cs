using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class ClientRepository : IClientRepository
{
    private readonly NohddxDbContext _db;

    public ClientRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<ClientMachine?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .Include(c => c.HardwareProfile)
            .Include(c => c.BootAssignment)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<ClientMachine?> GetByMacAddressAsync(string macAddress, CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .Include(c => c.HardwareProfile)
            .Include(c => c.BootAssignment)
            .FirstOrDefaultAsync(c => c.MacAddress == macAddress, ct);
    }

    public async Task<IReadOnlyList<ClientMachine>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .Include(c => c.HardwareProfile)
            .Include(c => c.BootAssignment)
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClientMachine>> GetByGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .Include(c => c.HardwareProfile)
            .Include(c => c.BootAssignment)
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClientMachine>> GetByNodeAsync(Guid nodeId, CancellationToken ct = default)
    {
        return await _db.Clients
            .Include(c => c.Group)
            .Include(c => c.HardwareProfile)
            .Include(c => c.BootAssignment)
            .Where(c => c.AssignedNodeId == nodeId)
            .OrderBy(c => c.Hostname)
            .ToListAsync(ct);
    }

    public async Task<ClientMachine> AddAsync(ClientMachine client, CancellationToken ct = default)
    {
        _db.Clients.Add(client);
        await _db.SaveChangesAsync(ct);
        return client;
    }

    public async Task UpdateAsync(ClientMachine client, CancellationToken ct = default)
    {
        _db.Clients.Update(client);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var client = await _db.Clients.FindAsync(new object[] { id }, ct);
        if (client is not null)
        {
            _db.Clients.Remove(client);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        return await _db.Clients.CountAsync(ct);
    }
}
