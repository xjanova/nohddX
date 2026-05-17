using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class BootAssignmentRepository : IBootAssignmentRepository
{
    private readonly NohddxDbContext _db;

    public BootAssignmentRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<BootAssignment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Assignments
            .Include(a => a.Client)
            .Include(a => a.Image)
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<IReadOnlyList<BootAssignment>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Assignments
            .Include(a => a.Client)
            .Include(a => a.Image)
            .Include(a => a.Profile)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<BootAssignment?> GetByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await _db.Assignments
            .Include(a => a.Client)
            .Include(a => a.Image)
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.ClientId == clientId, ct);
    }

    public async Task<IReadOnlyList<BootAssignment>> GetByImageIdAsync(Guid imageId, CancellationToken ct = default)
    {
        return await _db.Assignments
            .Include(a => a.Client)
            .Include(a => a.Image)
            .Include(a => a.Profile)
            .Where(a => a.ImageId == imageId)
            .ToListAsync(ct);
    }

    public async Task<BootAssignment> AddAsync(BootAssignment assignment, CancellationToken ct = default)
    {
        _db.Assignments.Add(assignment);
        await _db.SaveChangesAsync(ct);
        return assignment;
    }

    public async Task UpdateAsync(BootAssignment assignment, CancellationToken ct = default)
    {
        _db.Assignments.Update(assignment);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var assignment = await _db.Assignments.FindAsync(new object[] { id }, ct);
        if (assignment is not null)
        {
            _db.Assignments.Remove(assignment);
            await _db.SaveChangesAsync(ct);
        }
    }
}
