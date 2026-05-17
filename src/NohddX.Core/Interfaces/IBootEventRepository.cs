using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IBootEventRepository
{
    Task<BootEvent> AddAsync(BootEvent bootEvent, CancellationToken ct = default);
    Task<IReadOnlyList<BootEvent>> GetRecentAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<BootEvent>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
}
