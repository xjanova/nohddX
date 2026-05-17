using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Cluster.Failover;

public class FailoverManager : IFailoverManager
{
    private readonly ILoadBalancer _loadBalancer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FailoverManager> _logger;

    public FailoverManager(
        ILoadBalancer loadBalancer,
        IServiceScopeFactory scopeFactory,
        ILogger<FailoverManager> logger)
    {
        _loadBalancer = loadBalancer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task HandleNodeFailureAsync(Guid failedNodeId, CancellationToken ct = default)
    {
        _logger.LogWarning("Node {NodeId} failed, redistributing clients", failedNodeId);

        using var scope = _scopeFactory.CreateScope();
        var clientRepo = scope.ServiceProvider.GetRequiredService<IClientRepository>();

        IReadOnlyList<ClientMachine> orphanedClients;
        try
        {
            orphanedClients = await clientRepo.GetByNodeAsync(failedNodeId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve clients for failed node {NodeId}", failedNodeId);
            return;
        }

        if (orphanedClients.Count == 0)
        {
            _logger.LogInformation("No clients were assigned to failed node {NodeId}", failedNodeId);
            return;
        }

        _logger.LogInformation("Found {Count} orphaned clients from failed node {NodeId}",
            orphanedClients.Count, failedNodeId);

        var migratedCount = 0;
        var failedCount = 0;

        foreach (var client in orphanedClients)
        {
            try
            {
                var newNode = _loadBalancer.SelectNode(client);

                if (newNode is not null)
                {
                    client.AssignedNodeId = newNode.Id;
                    client.Status = ClientStatus.Migrating;
                    client.UpdatedAt = DateTime.UtcNow;
                    await clientRepo.UpdateAsync(client, ct);

                    client.Status = ClientStatus.Online;
                    await clientRepo.UpdateAsync(client, ct);

                    migratedCount++;
                    _logger.LogInformation("Client {Mac} migrated to node {NodeId}",
                        client.MacAddress, newNode.Id);
                }
                else
                {
                    client.Status = ClientStatus.Error;
                    client.AssignedNodeId = null;
                    await clientRepo.UpdateAsync(client, ct);
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate client {ClientId}", client.Id);
                failedCount++;
            }
        }

        _logger.LogInformation("Failover complete: {Migrated} migrated, {Failed} failed",
            migratedCount, failedCount);
    }

    public async Task MigrateClientAsync(Guid clientId, Guid targetNodeId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var clientRepo = scope.ServiceProvider.GetRequiredService<IClientRepository>();

        var client = await clientRepo.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            _logger.LogWarning("Client {ClientId} not found, skipping migration", clientId);
            return;
        }

        client.AssignedNodeId = targetNodeId;
        client.Status = ClientStatus.Migrating;
        client.UpdatedAt = DateTime.UtcNow;
        await clientRepo.UpdateAsync(client, ct);

        client.Status = ClientStatus.Online;
        await clientRepo.UpdateAsync(client, ct);

        _logger.LogInformation("Client {Mac} migrated to node {NodeId}",
            client.MacAddress, targetNodeId);
    }
}
