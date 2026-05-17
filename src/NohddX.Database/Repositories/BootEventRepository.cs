using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class BootEventRepository : IBootEventRepository
{
    private readonly NohddxDbContext _db;

    public BootEventRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<BootEvent> AddAsync(BootEvent bootEvent, CancellationToken ct = default)
    {
        _db.BootEvents.Add(bootEvent);
        await _db.SaveChangesAsync(ct);
        return bootEvent;
    }

    public async Task<IReadOnlyList<BootEvent>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        return await _db.BootEvents
            .OrderByDescending(e => e.StartedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BootEvent>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await _db.BootEvents
            .Where(e => e.ClientId == clientId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);
    }
}
