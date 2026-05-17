namespace NohddX.Api.DTOs;

public record CreateGroupRequest(
    string Name,
    string? Description = null,
    Guid? DefaultImageId = null);

public record UpdateGroupRequest(
    string? Name = null,
    string? Description = null,
    Guid? DefaultImageId = null);

public record GroupResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid? DefaultImageId,
    string? DefaultImageName,
    int ClientCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);
