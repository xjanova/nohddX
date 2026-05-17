using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;

namespace NohddX.Ui.Services;

/// <summary>
/// Wraps a SignalR connection to the server's <c>/hubs/dashboard</c>. Reconnects
/// automatically; rewires URL when <see cref="AppSettings"/> changes. Events
/// are marshalled onto the WPF dispatcher so view code-behinds can update
/// controls directly.
/// </summary>
public sealed class DashboardConnection : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private HubConnection? _hub;

    public DashboardConnection(AppSettings settings)
    {
        _settings = settings;
        _settings.Changed += async (_, _) => await ReconnectAsync();
    }

    public event Action<Guid, string>? ClientStatusChanged;
    public event Action<string>? StateChanged;

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;

    public async Task StartAsync()
    {
        await DisposeHubAsync();

        var url = _settings.ServerUrl.TrimEnd('/') + "/hubs/dashboard";

        _hub = new HubConnectionBuilder()
            .WithUrl(url, opts =>
            {
                if (!string.IsNullOrWhiteSpace(_settings.AdminApiKey))
                {
                    opts.Headers["X-Admin-Api-Key"] = _settings.AdminApiKey;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<Guid, string>("ClientStatusChanged", (id, status) =>
            OnDispatcher(() => ClientStatusChanged?.Invoke(id, status)));

        _hub.Closed += _ =>
        {
            OnDispatcher(() => StateChanged?.Invoke("Disconnected"));
            return Task.CompletedTask;
        };
        _hub.Reconnecting += _ =>
        {
            OnDispatcher(() => StateChanged?.Invoke("Reconnecting"));
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            OnDispatcher(() => StateChanged?.Invoke("Connected"));
            return Task.CompletedTask;
        };

        try
        {
            await _hub.StartAsync();
            OnDispatcher(() => StateChanged?.Invoke("Connected"));
        }
        catch
        {
            OnDispatcher(() => StateChanged?.Invoke("Disconnected"));
        }
    }

    private async Task ReconnectAsync()
    {
        try { await StartAsync(); }
        catch { /* swallow; StateChanged will reflect */ }
    }

    private static void OnDispatcher(Action a)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null || app.Dispatcher.CheckAccess())
            a();
        else
            app.Dispatcher.BeginInvoke(a);
    }

    private async Task DisposeHubAsync()
    {
        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch { /* best-effort */ }
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisposeHubAsync();
}
