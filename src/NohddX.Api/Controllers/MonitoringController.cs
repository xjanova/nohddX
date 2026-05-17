using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Core.Interfaces;
using NohddX.Monitoring.Alerts;
using NohddX.Monitoring.Health;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/monitoring")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class MonitoringController : ControllerBase
{
    private readonly HealthCheckService _healthCheck;
    private readonly AlertManager _alertManager;
    private readonly IAuditLogRepository _auditRepo;

    public MonitoringController(
        HealthCheckService healthCheck,
        AlertManager alertManager,
        IAuditLogRepository auditRepo)
    {
        _healthCheck = healthCheck;
        _alertManager = alertManager;
        _auditRepo = auditRepo;
    }

    [HttpGet("health")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealth(CancellationToken ct = default)
    {
        var report = await _healthCheck.GetOverallHealthAsync(ct);

        var response = new HealthResponse(
            IsHealthy: report.IsHealthy,
            CheckedAt: report.CheckedAt,
            Components: report.Components.Values.Select(c => new ComponentHealthResponse(
                Name: c.Name,
                IsHealthy: c.IsHealthy,
                Message: c.Message,
                ResponseTime: c.ResponseTime)).ToList());

        return Ok(response);
    }

    [HttpGet("alerts")]
    [ProducesResponseType(typeof(IEnumerable<AlertResponse>), StatusCodes.Status200OK)]
    public IActionResult GetAlerts([FromQuery] bool activeOnly = true)
    {
        var alerts = activeOnly
            ? _alertManager.GetActiveAlerts()
            : _alertManager.GetAllAlerts();

        var response = alerts.Select(a => new AlertResponse(
            Id: a.Id,
            Severity: a.Severity,
            Component: a.Component,
            Message: a.Message,
            CreatedAt: a.CreatedAt,
            Acknowledged: a.Acknowledged));

        return Ok(response);
    }

    /// <summary>
    /// Returns recent audit log entries — admin actions, agent registrations,
    /// image changes, etc. SECURITY.md flagged this endpoint as "future"; it
    /// completes the audit story by giving the operator a way to query log
    /// rows without opening the SQLite file directly. Capped at 1000 rows so
    /// a curious admin can't exhaust memory.
    /// </summary>
    [HttpGet("audit")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? actor = null,
        [FromQuery] string? action = null,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        // Clamp `take` so pathological values can't be used to OOM the server
        // or generate an enormous DB scan. 200 is the default; 1000 is hard cap.
        take = Math.Clamp(take, 1, 1000);

        IReadOnlyList<Core.Models.AuditLogEntry> entries;
        try
        {
            if (from is null && to is null && string.IsNullOrEmpty(actor) && string.IsNullOrEmpty(action))
            {
                // Fast path: no filters means latest N rows.
                entries = await _auditRepo.GetRecentAsync(take, ct);
            }
            else
            {
                entries = await _auditRepo.SearchAsync(from, to, actor, action, take, ct);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            // Schema drift: an older nohddx.db was created before AuditLog was
            // added to the model and EnsureCreated doesn't add tables to
            // existing DBs. The startup migration helper should already have
            // patched this — but if it ran in a different process, degrade
            // to an empty result instead of 500-ing.
            entries = Array.Empty<Core.Models.AuditLogEntry>();
        }

        var response = entries.Select(e => new AuditLogResponse(
            Id: e.Id,
            Timestamp: e.Timestamp,
            Actor: e.Actor,
            ActorId: e.ActorId,
            RemoteIp: e.RemoteIp,
            Action: e.Action,
            TargetType: e.TargetType,
            TargetId: e.TargetId,
            Success: e.Success,
            Detail: e.Detail));

        return Ok(response);
    }
}
