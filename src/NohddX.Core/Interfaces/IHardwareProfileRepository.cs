using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IHardwareProfileRepository
{
    Task<HardwareProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<HardwareProfile>> GetAllAsync(CancellationToken ct = default);
    Task<HardwareProfile> AddAsync(HardwareProfile profile, CancellationToken ct = default);
    Task UpdateAsync(HardwareProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
