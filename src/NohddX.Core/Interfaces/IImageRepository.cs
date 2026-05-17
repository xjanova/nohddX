using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IImageRepository
{
    Task<BootImage?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BootImage>> GetAllAsync(CancellationToken ct = default);
    Task<BootImage?> GetDefaultAsync(CancellationToken ct = default);
    Task<BootImage> AddAsync(BootImage image, CancellationToken ct = default);
    Task UpdateAsync(BootImage image, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
