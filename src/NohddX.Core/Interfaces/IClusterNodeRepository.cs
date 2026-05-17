using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IClusterNodeRepository
{
    Task<ClusterNode?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ClusterNode>> GetAllAsync(CancellationToken ct = default);
    Task<ClusterNode?> GetLeaderAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClusterNode>> GetOnlineNodesAsync(CancellationToken ct = default);
    Task<ClusterNode> AddAsync(ClusterNode node, CancellationToken ct = default);
    Task UpdateAsync(ClusterNode node, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
