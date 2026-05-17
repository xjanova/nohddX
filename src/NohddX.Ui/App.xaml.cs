using System.Windows;
using NohddX.Ui.Services;

namespace NohddX.Ui;

public partial class App : Application
{
    /// <summary>
    /// Persisted operator settings (server URL + admin API key). Single
    /// instance for the app lifetime — views read it on demand and listen
    /// to Changed for live updates.
    /// </summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// Typed HTTP client for the NohddX REST API. Rebuilt when Settings change.
    /// </summary>
    public static NohddxApiClient ApiClient { get; private set; } = null!;

    /// <summary>
    /// SignalR live connection for dashboard push notifications. Reconnects
    /// automatically; views subscribe to its events.
    /// </summary>
    public static DashboardConnection Dashboard { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();
        ApiClient = new NohddxApiClient(Settings);
        Dashboard = new DashboardConnection(Settings);

        // Fire-and-forget hub start; UI shows "Connecting..." until events arrive.
        _ = Dashboard.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await Dashboard.DisposeAsync();
            ApiClient.Dispose();
        }
        catch
        {
            // best-effort shutdown
        }
        base.OnExit(e);
    }
}
