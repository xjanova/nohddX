using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IBootAssignmentRepository
{
    Task<BootAssignment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BootAssignment>> GetAllAsync(CancellationToken ct = default);
    Task<BootAssignment?> GetByClientIdAsync(Guid clientId, CancellationToken ct = default);
    Task<IReadOnlyList<BootAssignment>> GetByImageIdAsync(Guid imageId, CancellationToken ct = default);
    Task<BootAssignment> AddAsync(BootAssignment assignment, CancellationToken ct = default);
    Task UpdateAsync(BootAssignment assignment, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
