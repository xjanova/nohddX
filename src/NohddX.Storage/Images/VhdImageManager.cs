using DiscUtils.Vhd;
using Microsoft.Extensions.Logging;

namespace NohddX.Storage.Images;

/// <summary>
/// Manages base VHD images using the DiscUtils library.
/// Provides operations for opening, creating, validating, and listing VHD files.
/// </summary>
public class VhdImageManager
{
    private readonly ILogger<VhdImageManager> _logger;

    static VhdImageManager()
    {
        // Register the VHD format with DiscUtils
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(Disk).Assembly);
    }

    public VhdImageManager(ILogger<VhdImageManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Retrieves information about a VHD image file.
    /// </summary>
    /// <returns>A tuple of (file size in bytes, virtual disk size in bytes, OS hint from geometry).</returns>
    public Task<(long SizeBytes, long DiskSize, string? OsHint)> GetImageInfoAsync(string vhdPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);

        if (!File.Exists(vhdPath))
        {
            throw new FileNotFoundException("VHD file not found.", vhdPath);
        }

        var fileInfo = new FileInfo(vhdPath);
        long fileSize = fileInfo.Length;
        long diskSize = 0;
        string? osHint = null;

        try
        {
            using var disk = new Disk(vhdPath, FileAccess.Read);
            diskSize = disk.Capacity;

            // Try to detect OS from disk content structure
            osHint = DetectOsHint(disk);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read VHD metadata for {VhdPath}. Returning file size only.", vhdPath);
        }

        return Task.FromResult((fileSize, diskSize, osHint));
    }

    /// <summary>
    /// Opens a base VHD image for read-only access and returns the content stream.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    public Task<Stream> OpenBaseImageAsync(string vhdPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);

        if (!File.Exists(vhdPath))
        {
            throw new FileNotFoundException("VHD file not found.", vhdPath);
        }

        // Open as a raw file stream for block-level CoW access
        var stream = new FileStream(vhdPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        _logger.LogDebug("Opened base image for reading: {VhdPath}", vhdPath);
        return Task.FromResult<Stream>(stream);
    }

    /// <summary>
    /// Creates a new empty dynamic VHD image at the specified path.
    /// </summary>
    public Task CreateBaseImageAsync(string vhdPath, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        string? directory = Path.GetDirectoryName(vhdPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(vhdPath))
        {
            throw new InvalidOperationException($"VHD file already exists at {vhdPath}.");
        }

        using var fs = new FileStream(vhdPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var disk = Disk.InitializeDynamic(fs, DiscUtils.Streams.Ownership.None, sizeBytes);
        _logger.LogInformation("Created dynamic VHD at {VhdPath} with capacity {SizeBytes} bytes.", vhdPath, sizeBytes);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates that a VHD file can be opened and read successfully.
    /// </summary>
    public Task<bool> ValidateImageAsync(string vhdPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vhdPath);

        if (!File.Exists(vhdPath))
        {
            _logger.LogWarning("VHD validation failed: file not found at {VhdPath}.", vhdPath);
            return Task.FromResult(false);
        }

        try
        {
            using var disk = new Disk(vhdPath, FileAccess.Read);
            // Check that we can read basic disk properties
            _ = disk.Capacity;
            _ = disk.Geometry;

            _logger.LogDebug("VHD validation passed for {VhdPath}.", vhdPath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VHD validation failed for {VhdPath}.", vhdPath);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Lists all VHD files in the specified directory with their info.
    /// </summary>
    public async Task<IReadOnlyList<VhdFileInfo>> GetImageListAsync(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        if (!Directory.Exists(basePath))
        {
            _logger.LogWarning("Image directory does not exist: {BasePath}", basePath);
            return Array.Empty<VhdFileInfo>();
        }

        var vhdFiles = Directory.GetFiles(basePath, "*.vhd", SearchOption.TopDirectoryOnly);
        var results = new List<VhdFileInfo>(vhdFiles.Length);

        foreach (var filePath in vhdFiles)
        {
            try
            {
                var (sizeBytes, diskSize, osHint) = await GetImageInfoAsync(filePath);
                results.Add(new VhdFileInfo
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    FileSizeBytes = sizeBytes,
                    DiskCapacityBytes = diskSize,
                    OsHint = osHint,
                    LastModified = File.GetLastWriteTimeUtc(filePath)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping VHD file that could not be read: {FilePath}", filePath);
            }
        }

        return results;
    }

    private static string? DetectOsHint(Disk disk)
    {
        try
        {
            // Attempt to detect OS by examining partition table
            var partitions = disk.Partitions;
            if (partitions is null || partitions.Count == 0)
            {
                return null;
            }

            foreach (var partition in partitions.Partitions)
            {
                string typeName = partition.TypeAsString ?? string.Empty;
                if (typeName.Contains("NTFS", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("FAT", StringComparison.OrdinalIgnoreCase))
                {
                    return "Windows";
                }

                if (typeName.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("ext", StringComparison.OrdinalIgnoreCase))
                {
                    return "Linux";
                }
            }
        }
        catch
        {
            // OS detection is best-effort
        }

        return null;
    }
}

/// <summary>
/// Information about a VHD file on disk.
/// </summary>
public class VhdFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public long DiskCapacityBytes { get; init; }
    public string? OsHint { get; init; }
    public DateTime LastModified { get; init; }
}
