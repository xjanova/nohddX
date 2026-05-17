using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;

namespace NohddX.Storage.CoW;

/// <summary>
/// Core Copy-on-Write disk implementation.
/// Each client gets its own overlay directory containing an overlay file and a block map.
/// Reads come from the base VHD; writes go to the per-client overlay.
/// </summary>
public class CowOverlayDisk : ICowStorageEngine
{
    private readonly NohddxOptions _options;
    private readonly ILogger<CowOverlayDisk> _logger;

    public CowOverlayDisk(IOptions<NohddxOptions> options, ILogger<CowOverlayDisk> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Stream> OpenDiskAsync(string baseImagePath, string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        if (!File.Exists(baseImagePath))
        {
            throw new FileNotFoundException("Base image not found.", baseImagePath);
        }

        string overlayDir = GetOverlayDirectory(clientId);

        // Open base image as read-only
        var baseStream = new FileStream(baseImagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: _options.CowBlockSizeBytes, useAsync: true);

        try
        {
            long baseImageSize = baseStream.Length;
            await EnsureOverlayExistsAsync(overlayDir, baseImageSize, ct);

            string overlayFilePath = Path.Combine(overlayDir, Constants.OverlayFileName);
            string blockMapPath = Path.Combine(overlayDir, Constants.BlockMapFileName);

            // Open overlay for read/write
            var overlayStream = new FileStream(overlayFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: _options.CowBlockSizeBytes, useAsync: true);

            // Load or create block map
            BlockMap blockMap;
            if (File.Exists(blockMapPath))
            {
                blockMap = await BlockMap.LoadAsync(blockMapPath);
                _logger.LogDebug("Loaded existing block map for client {ClientId} with {WrittenBlocks} written blocks.",
                    clientId, blockMap.WrittenBlockCount);
            }
            else
            {
                long totalBlocks = (baseImageSize + _options.CowBlockSizeBytes - 1) / _options.CowBlockSizeBytes;
                blockMap = new BlockMap(totalBlocks);
                _logger.LogDebug("Created new block map for client {ClientId} with {TotalBlocks} total blocks.",
                    clientId, totalBlocks);
            }

            _logger.LogInformation("Opened CoW disk for client {ClientId}. Base: {BaseImage}, Overlay: {OverlayDir}",
                clientId, baseImagePath, overlayDir);

            return new CowDiskStream(baseStream, overlayStream, blockMap, _options.CowBlockSizeBytes, blockMapPath);
        }
        catch
        {
            await baseStream.DisposeAsync();
            throw;
        }
    }

    public Task ResetOverlayAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        string overlayDir = GetOverlayDirectory(clientId);
        if (Directory.Exists(overlayDir))
        {
            Directory.Delete(overlayDir, recursive: true);
            _logger.LogInformation("Reset overlay for client {ClientId}. Deleted: {OverlayDir}", clientId, overlayDir);
        }
        else
        {
            _logger.LogDebug("No overlay directory found for client {ClientId} at {OverlayDir}.", clientId, overlayDir);
        }

        return Task.CompletedTask;
    }

    public Task<long> GetOverlaySizeAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        string overlayDir = GetOverlayDirectory(clientId);
        string overlayFilePath = Path.Combine(overlayDir, Constants.OverlayFileName);

        if (!File.Exists(overlayFilePath))
        {
            return Task.FromResult(0L);
        }

        var fileInfo = new FileInfo(overlayFilePath);
        return Task.FromResult(fileInfo.Length);
    }

    public async Task CreateSnapshotAsync(string clientId, string snapshotName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotName);

        string overlayDir = GetOverlayDirectory(clientId);
        if (!Directory.Exists(overlayDir))
        {
            throw new DirectoryNotFoundException($"Overlay directory not found for client {clientId}.");
        }

        string snapshotDir = Path.Combine(_options.SnapshotsPath, clientId, snapshotName);
        Directory.CreateDirectory(snapshotDir);

        // Copy overlay file
        string sourceOverlay = Path.Combine(overlayDir, Constants.OverlayFileName);
        string destOverlay = Path.Combine(snapshotDir, Constants.OverlayFileName);
        if (File.Exists(sourceOverlay))
        {
            await CopyFileAsync(sourceOverlay, destOverlay, ct);
        }

        // Copy block map
        string sourceBlockMap = Path.Combine(overlayDir, Constants.BlockMapFileName);
        string destBlockMap = Path.Combine(snapshotDir, Constants.BlockMapFileName);
        if (File.Exists(sourceBlockMap))
        {
            await CopyFileAsync(sourceBlockMap, destBlockMap, ct);
        }

        _logger.LogInformation(
            "Created snapshot '{SnapshotName}' for client {ClientId} at {SnapshotDir}.",
            snapshotName, clientId, snapshotDir);
    }

    private string GetOverlayDirectory(string clientId)
    {
        return Path.Combine(_options.OverlaysPath, clientId);
    }

    private async Task EnsureOverlayExistsAsync(string overlayDir, long baseImageSizeBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(overlayDir);

        string overlayFilePath = Path.Combine(overlayDir, Constants.OverlayFileName);
        if (!File.Exists(overlayFilePath))
        {
            // Create a sparse overlay file sized to match the base image
            await using var fs = new FileStream(overlayFilePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true);
            fs.SetLength(baseImageSizeBytes);

            _logger.LogDebug("Created overlay file at {OverlayPath} with size {Size} bytes.",
                overlayFilePath, baseImageSizeBytes);
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        const int bufferSize = 81920;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, useAsync: true);
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, useAsync: true);
        await source.CopyToAsync(dest, bufferSize, ct);
    }
}
