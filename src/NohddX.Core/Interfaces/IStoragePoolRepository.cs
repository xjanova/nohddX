using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IStoragePoolRepository
{
    Task<StoragePool?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StoragePool>> GetAllAsync(CancellationToken ct = default);
    Task<StoragePool?> GetDefaultAsync(CancellationToken ct = default);
    Task<StoragePool> AddAsync(StoragePool pool, CancellationToken ct = default);
    Task UpdateAsync(StoragePool pool, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
