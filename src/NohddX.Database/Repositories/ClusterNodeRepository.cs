using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class ClusterNodeRepository : IClusterNodeRepository
{
    private readonly NohddxDbContext _db;

    public ClusterNodeRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<ClusterNode?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.ClusterNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    public async Task<IReadOnlyList<ClusterNode>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.ClusterNodes
            .OrderBy(n => n.Hostname)
            .ToListAsync(ct);
    }

    public async Task<ClusterNode?> GetLeaderAsync(CancellationToken ct = default)
    {
        return await _db.ClusterNodes
            .FirstOrDefaultAsync(n => n.Role == ClusterRole.Leader, ct);
    }

    public async Task<IReadOnlyList<ClusterNode>> GetOnlineNodesAsync(CancellationToken ct = default)
    {
        return await _db.ClusterNodes
            .Where(n => n.Status == NodeStatus.Online)
            .OrderBy(n => n.Hostname)
            .ToListAsync(ct);
    }

    public async Task<ClusterNode> AddAsync(ClusterNode node, CancellationToken ct = default)
    {
        _db.ClusterNodes.Add(node);
        await _db.SaveChangesAsync(ct);
        return node;
    }

    public async Task UpdateAsync(ClusterNode node, CancellationToken ct = default)
    {
        _db.ClusterNodes.Update(node);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var node = await _db.ClusterNodes.FindAsync(new object[] { id }, ct);
        if (node is not null)
        {
            _db.ClusterNodes.Remove(node);
            await _db.SaveChangesAsync(ct);
        }
    }
}
