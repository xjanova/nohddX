using NohddX.Core.Models;

namespace NohddX.Api.DTOs;

public record StoragePoolResponse(
    Guid Id,
    string Name,
    string Path,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    string? RaidLevel,
    RaidStatus RaidStatus,
    int DiskCount,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record StorageHealthResponse(
    bool IsHealthy,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    IReadOnlyList<StoragePoolResponse> Pools);

public record DiskInfoResponse(
    string Device,
    string? VolumeLabel,
    string DriveFormat,
    string DriveType,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    bool IsReady,
    string Health);

/// <summary>
/// Physical disk row returned by <c>/api/storage/physical-disks</c>. Comes
/// from WMI on Windows (model, serial, SMART status, temperature when
/// available). Empty on non-Windows until a smartctl-backed Linux path
/// lands.
/// </summary>
public record PhysicalDiskResponse(
    string Model,
    string? SerialNumber,
    string? InterfaceType,
    string? MediaType,
    long? SizeBytes,
    uint? TemperatureCelsius,
    string Status,
    string Health,
    bool PredictFailure,
    string? SmartReason);
