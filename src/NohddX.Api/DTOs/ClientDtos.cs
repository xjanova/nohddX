namespace NohddX.Api.DTOs;

public record CreateClientRequest(
    string MacAddress,
    string? Hostname = null,
    Guid? GroupId = null);

public record UpdateClientRequest(
    string? Hostname = null,
    Guid? GroupId = null,
    Guid? HardwareProfileId = null);

public record AssignImageRequest(
    Guid ImageId,
    Guid? ProfileId = null);

public record BulkAssignRequest(
    Guid[] ClientIds,
    Guid ImageId);

public record BulkWakeRequest(
    Guid[] ClientIds);

public record ClientResponse(
    Guid Id,
    string MacAddress,
    string? Hostname,
    string? IpAddress,
    string Status,
    string? GroupName,
    Guid? GroupId,
    string? ImageName,
    Guid? ImageId,
    string? HardwareProfileName,
    Guid? HardwareProfileId,
    DateTime? LastSeen,
    DateTime? LastBootTime,
    DateTime CreatedAt);
