using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NohddX.Core.Models;

namespace NohddX.Storage.Raid;

/// <summary>
/// Monitors RAID array health via WMI (Windows Management Instrumentation).
/// Queries Win32_DiskDrive, MSFT_PhysicalDisk, and MSFT_StoragePool for disk and pool status.
/// Returns empty results gracefully on non-Windows platforms.
/// </summary>
public class RaidMonitor
{
    private readonly ILogger<RaidMonitor> _logger;

    /// <summary>
    /// Raised when a RAID status change is detected.
    /// </summary>
    public event EventHandler<RaidStatusChangedEventArgs>? RaidStatusChanged;

    public RaidMonitor(ILogger<RaidMonitor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Queries WMI for Windows Storage Spaces pools.
    /// Returns an empty list on non-Windows platforms.
    /// </summary>
    public Task<IReadOnlyList<StoragePool>> GetStoragePoolsAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("Storage pool query skipped: not running on Windows.");
            return Task.FromResult<IReadOnlyList<StoragePool>>(Array.Empty<StoragePool>());
        }

        return Task.FromResult<IReadOnlyList<StoragePool>>(QueryStoragePoolsWindows());
    }

    [SupportedOSPlatform("windows")]
    private List<StoragePool> QueryStoragePoolsWindows()
    {
        var pools = new List<StoragePool>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_StoragePool WHERE IsPrimordial = FALSE");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var pool = new StoragePool
                    {
                        Id = Guid.NewGuid(),
                        Name = GetWmiString(obj, "FriendlyName"),
                        Path = GetWmiString(obj, "UniqueId"),
                        TotalBytes = GetWmiLong(obj, "Size"),
                        UsedBytes = GetWmiLong(obj, "AllocatedSize"),
                        RaidStatus = MapHealthStatus(GetWmiInt(obj, "HealthStatus")),
                        IsDefault = GetWmiBool(obj, "IsReadOnly") == false,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    pool.FreeBytes = pool.TotalBytes - pool.UsedBytes;

                    // Try to get disk count from associated physical disks
                    pool.DiskCount = GetPhysicalDiskCount(pool.Path);

                    pools.Add(pool);
                }
            }

            _logger.LogInformation("Found {Count} storage pool(s).", pools.Count);
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(ex, "Failed to query WMI for storage pools.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying storage pools.");
        }

        return pools;
    }

    /// <summary>
    /// Queries WMI for physical disk health information.
    /// </summary>
    public Task<IReadOnlyList<DiskHealthInfo>> GetDiskHealthAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("Disk health query skipped: not running on Windows.");
            return Task.FromResult<IReadOnlyList<DiskHealthInfo>>(Array.Empty<DiskHealthInfo>());
        }

        return Task.FromResult<IReadOnlyList<DiskHealthInfo>>(QueryDiskHealthWindows());
    }

    [SupportedOSPlatform("windows")]
    private List<DiskHealthInfo> QueryDiskHealthWindows()
    {
        var disks = new List<DiskHealthInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_PhysicalDisk");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var disk = new DiskHealthInfo
                    {
                        DeviceId = GetWmiString(obj, "DeviceId"),
                        FriendlyName = GetWmiString(obj, "FriendlyName"),
                        SerialNumber = GetWmiString(obj, "SerialNumber"),
                        MediaType = MapMediaType(GetWmiInt(obj, "MediaType")),
                        SizeBytes = GetWmiLong(obj, "Size"),
                        HealthStatus = MapHealthStatus(GetWmiInt(obj, "HealthStatus")),
                        OperationalStatus = MapOperationalStatus(GetWmiInt(obj, "OperationalStatus")),
                        BusType = GetWmiString(obj, "BusType")
                    };

                    disks.Add(disk);
                }
            }

            _logger.LogInformation("Found {Count} physical disk(s).", disks.Count);
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(ex, "Failed to query WMI for physical disks.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying disk health.");
        }

        return disks;
    }

    /// <summary>
    /// Raises the RaidStatusChanged event.
    /// </summary>
    protected virtual void OnRaidStatusChanged(RaidStatusChangedEventArgs e)
    {
        RaidStatusChanged?.Invoke(this, e);
    }

    [SupportedOSPlatform("windows")]
    private int GetPhysicalDiskCount(string poolUniqueId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_PhysicalDisk");

            int count = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    count++;
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static RaidStatus MapHealthStatus(int healthStatus)
    {
        // MSFT_StoragePool / MSFT_PhysicalDisk HealthStatus values:
        // 0 = Healthy, 1 = Warning, 2 = Unhealthy, 5 = Unknown
        return healthStatus switch
        {
            0 => RaidStatus.Healthy,
            1 => RaidStatus.Degraded,
            2 => RaidStatus.Failed,
            _ => RaidStatus.Unknown
        };
    }

    private static string MapMediaType(int mediaType)
    {
        return mediaType switch
        {
            0 => "Unspecified",
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => "Unknown"
        };
    }

    private static string MapOperationalStatus(int status)
    {
        return status switch
        {
            0 => "Unknown",
            1 => "Other",
            2 => "OK",
            3 => "Degraded",
            5 => "Predictive Failure",
            6 => "Error",
            0xD => "Starting",
            0x11 => "In Service",
            _ => $"Status-{status}"
        };
    }

    [SupportedOSPlatform("windows")]
    private static string GetWmiString(ManagementObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long GetWmiLong(ManagementObject obj, string propertyName)
    {
        try
        {
            object? value = obj[propertyName];
            return value is not null ? Convert.ToInt64(value) : 0;
        }
        catch
        {
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int GetWmiInt(ManagementObject obj, string propertyName)
    {
        try
        {
            object? value = obj[propertyName];
            return value is not null ? Convert.ToInt32(value) : -1;
        }
        catch
        {
            return -1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool GetWmiBool(ManagementObject obj, string propertyName)
    {
        try
        {
            object? value = obj[propertyName];
            return value is not null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Information about the health of a physical disk.
/// </summary>
public class DiskHealthInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public RaidStatus HealthStatus { get; init; }
    public string OperationalStatus { get; init; } = string.Empty;
    public string BusType { get; init; } = string.Empty;
}

/// <summary>
/// Event arguments for RAID status change events.
/// </summary>
public class RaidStatusChangedEventArgs : EventArgs
{
    public required string PoolName { get; init; }
    public RaidStatus PreviousStatus { get; init; }
    public RaidStatus CurrentStatus { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
