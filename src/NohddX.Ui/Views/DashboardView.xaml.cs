using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NohddX.Api.DTOs;
using NohddX.Ui.Helpers;
using SkiaSharp;

namespace NohddX.Ui.Views;

public partial class DashboardView : UserControl
{
    // The throughput chart is decorative until /api/monitoring exposes real
    // throughput. We seed it with zeros and let the live update tick advance
    // it; cells will animate as soon as the server starts pushing metrics.
    private readonly List<ObservableValue> _txValues = new();
    private readonly List<ObservableValue> _rxValues = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<BootEvent> _bootEvents = new();

    private IReadOnlyList<ClientResponse> _lastClients = Array.Empty<ClientResponse>();

    public DashboardView()
    {
        InitializeComponent();

        BuildRack();
        BuildChart();
        BootEventsList.ItemsSource = _bootEvents;

        // Periodic counts refresh (every 5s) — cheap, the server holds it
        // all in EF Core with SQLite locally.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        Loaded += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => _refreshTimer.Stop();

        App.Dashboard.ClientStatusChanged += OnClientStatusChanged;
    }

    // ── Data refresh ────────────────────────────────────

    private async Task RefreshAsync()
    {
        try
        {
            _lastClients = await App.ApiClient.GetClientsAsync();
            var images = await App.ApiClient.GetImagesAsync();

            UpdateCounts(images.Count);
        }
        catch
        {
            // Leave previous values on screen if the API blips
        }
    }

    private void UpdateCounts(int imageCount)
    {
        int online = _lastClients.Count(c => c.Status == "Online");
        int booting = _lastClients.Count(c => c.Status == "Booting");

        TotalClientsText.Text = _lastClients.Count.ToString();
        OnlineNowText.Text = online.ToString();
        BootingText.Text = booting.ToString();
        BootImagesText.Text = imageCount.ToString();
    }

    private void OnClientStatusChanged(Guid clientId, string status)
    {
        // Reflect the push immediately without waiting for the 5s refresh.
        var match = _lastClients.FirstOrDefault(c => c.Id == clientId);
        if (match is not null)
        {
            var updated = match with { Status = status };
            _lastClients = _lastClients.Select(c => c.Id == clientId ? updated : c).ToList();
            UpdateCounts(int.TryParse(BootImagesText.Text, out var n) ? n : 0);
        }

        _bootEvents.Insert(0, new BootEvent(
            DateTime.Now.ToString("HH:mm:ss"),
            match?.MacAddress ?? clientId.ToString("N").Substring(0, 12).ToUpper(),
            $"Status -> {status}",
            status is "Online" or "Booting"));

        while (_bootEvents.Count > 20) _bootEvents.RemoveAt(_bootEvents.Count - 1);
    }

    // ── 3D Rack (decorative) ────────────────────────────

    private void BuildRack()
    {
        var frame = Viewport3DHelper.CreateRackFrame(6);
        RackModelGroup.Children.Add(frame);

        var servers = new[]
        {
            (y: 0.175, color: Color.FromRgb(63, 185, 80)),
            (y: 0.525, color: Color.FromRgb(63, 185, 80)),
            (y: 0.875, color: Color.FromRgb(210, 153, 34)),
            (y: 1.225, color: Color.FromRgb(63, 185, 80)),
            (y: 1.575, color: Color.FromRgb(248, 81, 73)),
            (y: 1.925, color: Color.FromRgb(63, 185, 80)),
        };

        foreach (var (y, color) in servers)
        {
            var node = Viewport3DHelper.CreateServerNode(0, y, 0, color);
            RackModelGroup.Children.Add(node);
        }
    }

    // ── Live Chart (placeholder until /api/monitoring streams metrics) ─

    private void BuildChart()
    {
        for (int i = 0; i < 30; i++)
        {
            _txValues.Add(new ObservableValue(0));
            _rxValues.Add(new ObservableValue(0));
        }

        ThroughputChart.Series = new ISeries[]
        {
            new LineSeries<ObservableValue>
            {
                Values = _txValues,
                Name = "TX (Mbps)",
                GeometrySize = 0,
                Stroke = new SolidColorPaint(SKColors.DeepSkyBlue, 2),
                Fill = new SolidColorPaint(SKColors.DeepSkyBlue.WithAlpha(40)),
                LineSmoothness = 0.65,
            },
            new LineSeries<ObservableValue>
            {
                Values = _rxValues,
                Name = "RX (Mbps)",
                GeometrySize = 0,
                Stroke = new SolidColorPaint(new SKColor(188, 140, 255), 2),
                Fill = new SolidColorPaint(new SKColor(188, 140, 255, 40)),
                LineSmoothness = 0.65,
            }
        };

        ThroughputChart.XAxes = new Axis[]
        {
            new Axis { ShowSeparatorLines = false, IsVisible = false }
        };

        ThroughputChart.YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(176, 184, 196)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(61, 68, 77)),
                MinLimit = 0,
            }
        };

        ThroughputChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Top;
        ThroughputChart.LegendTextPaint = new SolidColorPaint(new SKColor(176, 184, 196));
        ThroughputChart.DrawMargin = new LiveChartsCore.Measure.Margin(8, 8, 8, 8);
    }

    public record BootEvent(string Timestamp, string MacAddress, string Message, bool IsSuccess);
}
