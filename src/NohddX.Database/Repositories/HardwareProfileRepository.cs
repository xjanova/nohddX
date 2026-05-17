using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class HardwareProfileRepository : IHardwareProfileRepository
{
    private readonly NohddxDbContext _db;

    public HardwareProfileRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<HardwareProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.HardwareProfiles
            .Include(p => p.Clients)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<IReadOnlyList<HardwareProfile>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.HardwareProfiles
            .Include(p => p.Clients)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<HardwareProfile> AddAsync(HardwareProfile profile, CancellationToken ct = default)
    {
        _db.HardwareProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task UpdateAsync(HardwareProfile profile, CancellationToken ct = default)
    {
        _db.HardwareProfiles.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await _db.HardwareProfiles.FindAsync(new object[] { id }, ct);
        if (profile is not null)
        {
            _db.HardwareProfiles.Remove(profile);
            await _db.SaveChangesAsync(ct);
        }
    }
}
