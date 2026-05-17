using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly NohddxDbContext _db;

    public AuditLogRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<AuditLogEntry> AddAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int take = 100, CancellationToken ct = default)
    {
        return await _db.AuditLog
            .OrderByDescending(e => e.Timestamp)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> SearchAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? actor = null,
        string? action = null,
        int take = 500,
        CancellationToken ct = default)
    {
        var q = _db.AuditLog.AsQueryable();
        if (from.HasValue) q = q.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) q = q.Where(e => e.Timestamp <= to.Value);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(e => e.Actor == actor);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(e => e.Action == action);

        return await q.OrderByDescending(e => e.Timestamp).Take(take).ToListAsync(ct);
    }
}
