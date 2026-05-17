using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IClientGroupRepository
{
    Task<ClientGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ClientGroup>> GetAllAsync(CancellationToken ct = default);
    Task<ClientGroup> AddAsync(ClientGroup group, CancellationToken ct = default);
    Task UpdateAsync(ClientGroup group, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
