using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class ClientGroupRepository : IClientGroupRepository
{
    private readonly NohddxDbContext _db;

    public ClientGroupRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<ClientGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Groups
            .Include(g => g.Clients)
            .Include(g => g.DefaultImage)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<IReadOnlyList<ClientGroup>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Groups
            .Include(g => g.Clients)
            .Include(g => g.DefaultImage)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<ClientGroup> AddAsync(ClientGroup group, CancellationToken ct = default)
    {
        _db.Groups.Add(group);
        await _db.SaveChangesAsync(ct);
        return group;
    }

    public async Task UpdateAsync(ClientGroup group, CancellationToken ct = default)
    {
        _db.Groups.Update(group);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var group = await _db.Groups.FindAsync(new object[] { id }, ct);
        if (group is not null)
        {
            _db.Groups.Remove(group);
            await _db.SaveChangesAsync(ct);
        }
    }
}
