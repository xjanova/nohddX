using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class StoragePoolRepository : IStoragePoolRepository
{
    private readonly NohddxDbContext _db;

    public StoragePoolRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<StoragePool?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.StoragePools.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<IReadOnlyList<StoragePool>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.StoragePools
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<StoragePool?> GetDefaultAsync(CancellationToken ct = default)
    {
        return await _db.StoragePools
            .FirstOrDefaultAsync(p => p.IsDefault, ct);
    }

    public async Task<StoragePool> AddAsync(StoragePool pool, CancellationToken ct = default)
    {
        _db.StoragePools.Add(pool);
        await _db.SaveChangesAsync(ct);
        return pool;
    }

    public async Task UpdateAsync(StoragePool pool, CancellationToken ct = default)
    {
        _db.StoragePools.Update(pool);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var pool = await _db.StoragePools.FindAsync(new object[] { id }, ct);
        if (pool is not null)
        {
            _db.StoragePools.Remove(pool);
            await _db.SaveChangesAsync(ct);
        }
    }
}
