namespace NohddX.Api.DTOs;

public record HealthResponse(
    bool IsHealthy,
    DateTime CheckedAt,
    IReadOnlyList<ComponentHealthResponse> Components);

public record ComponentHealthResponse(
    string Name,
    bool IsHealthy,
    string? Message,
    TimeSpan? ResponseTime);

public record AlertResponse(
    Guid Id,
    string Severity,
    string Component,
    string Message,
    DateTime CreatedAt,
    bool Acknowledged);

public record AuditLogResponse(
    Guid Id,
    DateTime Timestamp,
    string Actor,
    string? ActorId,
    string? RemoteIp,
    string Action,
    string? TargetType,
    string? TargetId,
    bool Success,
    string? Detail);
