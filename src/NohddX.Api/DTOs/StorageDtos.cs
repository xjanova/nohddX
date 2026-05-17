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
