using NohddX.Core.Models;

namespace NohddX.Api.DTOs;

public record CreateImageRequest(
    string Name,
    OsType OsType,
    string Version,
    string FilePath,
    long SizeBytes,
    string? Checksum = null,
    bool IsDefault = false);

public record UpdateImageRequest(
    string? Name = null,
    string? Version = null,
    string? FilePath = null,
    long? SizeBytes = null,
    string? Checksum = null,
    ImageStatus? Status = null,
    bool? IsDefault = null);

public record CreateSnapshotRequest(
    string Name,
    string? Description = null);

public record ImageResponse(
    Guid Id,
    string Name,
    OsType OsType,
    string Version,
    string FilePath,
    long SizeBytes,
    string? Checksum,
    ImageStatus Status,
    bool IsDefault,
    int AssignmentCount,
    int SnapshotCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record SnapshotResponse(
    Guid Id,
    Guid ImageId,
    string Name,
    string? Description,
    string SnapshotPath,
    long SizeBytes,
    DateTime CreatedAt);
