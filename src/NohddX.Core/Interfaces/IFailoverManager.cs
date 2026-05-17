using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IFailoverManager
{
    Task HandleNodeFailureAsync(Guid nodeId, CancellationToken ct = default);
    Task MigrateClientAsync(Guid clientId, Guid targetNodeId, CancellationToken ct = default);
}
