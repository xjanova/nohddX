using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NohddX.Monitoring.Alerts;

/// <summary>
/// Represents a system alert with severity, component, and acknowledgement state.
/// </summary>
public record Alert(
    Guid Id,
    string Severity,
    string Component,
    string Message,
    DateTime CreatedAt,
    bool Acknowledged);

/// <summary>
/// Manages system alerts with thread-safe in-memory storage.
/// Supports registering, querying, and acknowledging alerts.
/// </summary>
public class AlertManager
{
    private readonly ConcurrentDictionary<Guid, Alert> _alerts = new();
    private readonly ILogger<AlertManager> _logger;

    /// <summary>
    /// Raised whenever a new alert is registered.
    /// </summary>
    public event EventHandler<Alert>? AlertRaised;

    public AlertManager(ILogger<AlertManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a new alert and fires the AlertRaised event.
    /// </summary>
    /// <param name="severity">Alert severity: "Critical", "Warning", "Info".</param>
    /// <param name="component">The component that raised the alert (e.g. "Storage", "iSCSI").</param>
    /// <param name="message">Human-readable description of the alert condition.</param>
    /// <returns>The newly created alert.</returns>
    public Alert RegisterAlert(string severity, string component, string message)
    {
        var alert = new Alert(
            Id: Guid.NewGuid(),
            Severity: severity,
            Component: component,
            Message: message,
            CreatedAt: DateTime.UtcNow,
            Acknowledged: false);

        _alerts[alert.Id] = alert;
        _logger.LogWarning("[{Severity}] {Component}: {Message}", severity, component, message);

        AlertRaised?.Invoke(this, alert);

        return alert;
    }

    /// <summary>
    /// Returns all active (unacknowledged) alerts ordered by creation time descending.
    /// </summary>
    public IReadOnlyList<Alert> GetActiveAlerts()
    {
        return _alerts.Values
            .Where(a => !a.Acknowledged)
            .OrderByDescending(a => a.CreatedAt)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Returns all alerts (including acknowledged) ordered by creation time descending.
    /// </summary>
    public IReadOnlyList<Alert> GetAllAlerts()
    {
        return _alerts.Values
            .OrderByDescending(a => a.CreatedAt)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Acknowledges an alert by its ID. Returns true if the alert was found and updated.
    /// </summary>
    public bool AcknowledgeAlert(Guid alertId)
    {
        if (!_alerts.TryGetValue(alertId, out var existing))
        {
            _logger.LogWarning("Attempted to acknowledge non-existent alert {Id}", alertId);
            return false;
        }

        var acknowledged = existing with { Acknowledged = true };
        _alerts[alertId] = acknowledged;
        _logger.LogInformation("Alert {Id} acknowledged ({Component}: {Message})",
            alertId, existing.Component, existing.Message);
        return true;
    }

    /// <summary>
    /// Removes all acknowledged alerts from the store.
    /// </summary>
    public int ClearAcknowledgedAlerts()
    {
        var toRemove = _alerts.Values.Where(a => a.Acknowledged).Select(a => a.Id).ToList();
        foreach (var id in toRemove)
        {
            _alerts.TryRemove(id, out _);
        }

        _logger.LogInformation("Cleared {Count} acknowledged alert(s)", toRemove.Count);
        return toRemove.Count;
    }
}
