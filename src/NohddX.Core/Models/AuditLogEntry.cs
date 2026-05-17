namespace NohddX.Core.Models;

/// <summary>
/// One row per security-sensitive action (admin auth, image change,
/// client assignment, agent registration, etc.). Append-only; never updated.
/// </summary>
public class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>"admin", "agent", "anonymous"</summary>
    public string Actor { get; set; } = "anonymous";

    /// <summary>
    /// For agents this is the agent (client) GUID; for admins this is the
    /// API-key fingerprint (first 8 hex chars of SHA-256). Null for
    /// anonymous boot script requests.
    /// </summary>
    public string? ActorId { get; set; }

    public string? RemoteIp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public bool Success { get; set; }
    public string? Detail { get; set; }
}
