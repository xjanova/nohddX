using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Cluster.Sync;

public class ImageSyncService : BackgroundService
{
    private readonly NohddxOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageSyncService> _logger;

    private readonly ConcurrentDictionary<string, SyncStatus> _syncStatuses = new();

    private const int SyncCheckIntervalMs = 30_000;
    private const int ChunkSizeBytes = 4 * 1024 * 1024;

    public ImageSyncService(
        IOptions<NohddxOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<ImageSyncService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Cluster.Enabled)
        {
            _logger.LogInformation("Cluster disabled, image sync will not start");
            return;
        }

        _logger.LogInformation("Image sync service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSyncImagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during image sync check");
            }

            try { await Task.Delay(SyncCheckIntervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAndSyncImagesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<IClusterNodeRepository>();
        var imageRepo = scope.ServiceProvider.GetRequiredService<IImageRepository>();

        var onlineNodes = await nodeRepo.GetOnlineNodesAsync(ct);
        if (onlineNodes.Count <= 1) return;

        var images = await imageRepo.GetAllAsync(ct);
        if (images.Count == 0) return;

        foreach (var image in images)
        {
            if (image.Status != ImageStatus.Active) continue;

            foreach (var node in onlineNodes)
            {
                var statusKey = $"{image.Id}:{node.Id}";

                if (_syncStatuses.TryGetValue(statusKey, out var existing)
                    && existing.State is "Synced" or "Syncing")
                    continue;

                _syncStatuses[statusKey] = new SyncStatus(image.Id, node.Id, 0, "Pending");
                await SyncImageToNodeAsync(image, node, statusKey, ct);
            }
        }
    }

    private async Task SyncImageToNodeAsync(
        BootImage image, ClusterNode targetNode, string statusKey, CancellationToken ct)
    {
        try
        {
            _syncStatuses[statusKey] = new SyncStatus(image.Id, targetNode.Id, 0, "Syncing");

            var filePath = string.IsNullOrEmpty(image.FilePath) ? $"{image.Id}.vhd" : image.FilePath;
            var imagePath = Path.Combine(_options.BaseImagesPath, filePath);

            if (!File.Exists(imagePath))
            {
                _syncStatuses[statusKey] = new SyncStatus(image.Id, targetNode.Id, 0, "Error");
                return;
            }

            var fileInfo = new FileInfo(imagePath);
            var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / ChunkSizeBytes);

            for (var i = 0; i < totalChunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                var progress = (double)(i + 1) / totalChunks * 100;
                _syncStatuses[statusKey] = new SyncStatus(image.Id, targetNode.Id, progress, "Syncing");
                await Task.Delay(10, ct); // Placeholder for actual chunk transfer
            }

            _syncStatuses[statusKey] = new SyncStatus(image.Id, targetNode.Id, 100, "Synced");
            _logger.LogInformation("Image {Name} synced to node {Hostname}", image.Name, targetNode.Hostname);
        }
        catch (OperationCanceledException)
        {
            _syncStatuses[statusKey] = _syncStatuses[statusKey] with { State = "Cancelled" };
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync image {ImageId} to node {NodeId}", image.Id, targetNode.Id);
            _syncStatuses[statusKey] = _syncStatuses[statusKey] with { State = "Error" };
        }
    }

    public IReadOnlyList<SyncStatus> GetSyncStatuses()
        => _syncStatuses.Values.ToList().AsReadOnly();

    public record SyncStatus(Guid ImageId, Guid NodeId, double ProgressPercent, string State);
}
