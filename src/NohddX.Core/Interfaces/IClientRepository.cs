using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IClientRepository
{
    Task<ClientMachine?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ClientMachine?> GetByMacAddressAsync(string macAddress, CancellationToken ct = default);
    Task<IReadOnlyList<ClientMachine>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClientMachine>> GetByGroupAsync(Guid groupId, CancellationToken ct = default);
    Task<IReadOnlyList<ClientMachine>> GetByNodeAsync(Guid nodeId, CancellationToken ct = default);
    Task<ClientMachine> AddAsync(ClientMachine client, CancellationToken ct = default);
    Task UpdateAsync(ClientMachine client, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> GetCountAsync(CancellationToken ct = default);
}
