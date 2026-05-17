using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NohddX.Ui.ViewModels;

namespace NohddX.Ui.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _pingTimer;
    private readonly DateTime _startTime;

    public MainWindow()
    {
        InitializeComponent();

        _vm = (MainViewModel)DataContext;
        _vm.ServerAddress = App.Settings.ServerUrl;
        _startTime = DateTime.UtcNow;

        // Uptime ticker (1-second interval)
        _uptimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uptimeTimer.Tick += UptimeTick;
        _uptimeTimer.Start();

        // Server reachability ticker: ping every 5 seconds and update the LED.
        _pingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pingTimer.Tick += async (_, _) => await PingAndUpdateStatusAsync();
        _pingTimer.Start();
        _ = PingAndUpdateStatusAsync(); // fire one immediately

        // Reflect dashboard hub state too — gives faster feedback than 5s ping.
        App.Dashboard.StateChanged += s =>
        {
            // StateChanged is dispatched on UI thread by DashboardConnection.
            if (s == "Connected")
            {
                _vm.ServerStatus = "Online";
                UpdateServerLed("Online");
            }
        };

        App.Settings.Changed += (_, _) => _vm.ServerAddress = App.Settings.ServerUrl;

        // Set initial content to Dashboard
        SetContent("Dashboard");
    }

    private async Task PingAndUpdateStatusAsync()
    {
        var ok = await App.ApiClient.PingAsync();
        if (ok)
        {
            _vm.ServerStatus = "Online";
            UpdateServerLed("Online");
        }
        else
        {
            _vm.ServerStatus = "Offline";
            UpdateServerLed("Offline");
        }
    }

    // ── Window chrome handlers ──

    private void MinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        _uptimeTimer.Stop();
        _pingTimer.Stop();
        Close();
    }

    // ── Navigation ──

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection fires during InitializeComponent (before _vm is set) — guard against it.
        if (_vm is null) return;

        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string view)
        {
            _vm.Navigate(view);
            SetContent(view);
        }
    }

    // Cache view instances so state is preserved when switching tabs
    private readonly Dictionary<string, UIElement> _viewCache = new();

    private void SetContent(string view)
    {
        if (!_viewCache.TryGetValue(view, out var viewElement))
        {
            viewElement = CreateView(view);
            _viewCache[view] = viewElement;
        }

        ContentArea.Content = viewElement;
    }

    private static UIElement CreateView(string viewName) => viewName switch
    {
        "Dashboard" => new DashboardView(),
        "Clients"   => new ClientsView(),
        "Images"    => new ImagesView(),
        "Cluster"   => new ClusterView(),
        "Storage"   => new StorageView(),
        "Settings"  => new SettingsView(),
        _ => new System.Windows.Controls.TextBlock
        {
            Text = viewName,
            FontSize = 20,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ChromeWhiteBrush")
        }
    };

    // ── Status updates ──

    private void UptimeTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        _vm.Uptime = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void UpdateServerLed(string status)
    {
        var styleName = status switch
        {
            "Online" => "StatusLedOnline",
            "Error" => "StatusLedError",
            "Booting" => "StatusLedBooting",
            _ => "StatusLedOffline"
        };

        if (FindResource(styleName) is Style style)
        {
            ServerLed.Style = style;
        }
    }
}
