using Microsoft.AspNetCore.SignalR;
using NohddX.Core.Models;
using NohddX.Monitoring.Alerts;

namespace NohddX.Api.Hubs;

public class DashboardNotifier
{
    private readonly IHubContext<DashboardHub> _hubContext;

    public DashboardNotifier(IHubContext<DashboardHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyClientStatusChangedAsync(Guid clientId, string status)
    {
        await _hubContext.Clients.All.SendAsync("ClientStatusChanged", clientId, status);
    }

    public async Task NotifyBootEventOccurredAsync(BootEvent bootEvent)
    {
        await _hubContext.Clients.All.SendAsync("BootEventOccurred", bootEvent);
    }

    public async Task NotifyMetricsUpdatedAsync(object metrics)
    {
        await _hubContext.Clients.All.SendAsync("MetricsUpdated", metrics);
    }

    public async Task NotifyClusterStateChangedAsync()
    {
        await _hubContext.Clients.All.SendAsync("ClusterStateChanged");
    }

    public async Task NotifyAlertRaisedAsync(Alert alert)
    {
        await _hubContext.Clients.All.SendAsync("AlertRaised", alert);
    }
}
