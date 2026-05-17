using Microsoft.AspNetCore.SignalR;

namespace NohddX.Api.Hubs;

public class DashboardHub : Hub
{
    // Client methods sent from server:
    // ClientStatusChanged(clientId, status)
    // BootEventOccurred(bootEvent)
    // MetricsUpdated(metrics)
    // ClusterStateChanged(nodes)
    // AlertRaised(alert)

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
