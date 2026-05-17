using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IAuditLogRepository
{
    Task<AuditLogEntry> AddAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int take = 100, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> SearchAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? actor = null,
        string? action = null,
        int take = 500,
        CancellationToken ct = default);
}
