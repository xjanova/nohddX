using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NohddX.Core.Interfaces;

namespace NohddX.ClientMgmt.Services;

/// <summary>
/// Re-populates the in-memory iSCSI <see cref="TargetRegistry"/> from active
/// boot assignments in the database when the server starts. Without this, the
/// registry is empty after a server restart and every iPXE login fails until
/// an operator re-assigns each client.
/// </summary>
public sealed class IscsiTargetBootstrap : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IscsiTargetBootstrap> _logger;

    public IscsiTargetBootstrap(
        IServiceScopeFactory scopeFactory,
        ILogger<IscsiTargetBootstrap> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var assignments = scope.ServiceProvider.GetRequiredService<IBootAssignmentRepository>();
            var images = scope.ServiceProvider.GetRequiredService<IImageRepository>();
            var iscsi = scope.ServiceProvider.GetRequiredService<IIscsiTargetManager>();

            var active = (await assignments.GetAllAsync(cancellationToken))
                .Where(a => a.IsActive)
                .ToList();

            int registered = 0;
            foreach (var a in active)
            {
                var img = await images.GetByIdAsync(a.ImageId, cancellationToken);
                if (img is null || string.IsNullOrWhiteSpace(img.FilePath))
                {
                    _logger.LogWarning(
                        "Skipping iSCSI target for client {ClientId}: image {ImageId} missing or has no FilePath",
                        a.ClientId, a.ImageId);
                    continue;
                }

                await iscsi.RegisterTargetAsync(a.ClientId.ToString(), img.FilePath, cancellationToken);
                registered++;
            }

            _logger.LogInformation(
                "iSCSI target bootstrap: registered {Registered}/{Total} active assignments",
                registered, active.Count);
        }
        catch (Exception ex)
        {
            // Don't crash the server if bootstrap fails — log loudly so the
            // operator knows clients won't boot until they reassign.
            _logger.LogError(ex, "iSCSI target bootstrap failed; clients will fail to boot until reassigned");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
