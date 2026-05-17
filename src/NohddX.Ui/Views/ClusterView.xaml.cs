using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NohddX.Api.DTOs;
using NohddX.Core.Models;
using NohddX.Ui.Helpers;

namespace NohddX.Ui.Views;

public partial class ClusterView : UserControl
{
    private readonly DispatcherTimer _refreshTimer;

    public ClusterView()
    {
        InitializeComponent();

        // Static frame is built once. Node visualizations get rebuilt on each
        // refresh because their count + colors depend on cluster state.
        var frame = Viewport3DHelper.CreateRackFrame(4);
        ClusterModelGroup.Children.Add(frame);

        Loaded += async (_, _) => await RefreshAsync();

        // Cluster state changes slowly — 10s polling is fine; SignalR's
        // ClusterStateChanged also wakes us.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private async Task RefreshAsync()
    {
        var status = await App.ApiClient.GetClusterStatusAsync();

        if (status is null || status.Nodes.Count == 0)
        {
            RenderEmpty();
            return;
        }

        RenderNodes(status.Nodes);
    }

    private void RenderEmpty()
    {
        NodesGrid.ItemsSource = new[]
        {
            new ClusterNodeInfo("(standalone)", "Local", 0, 0, 0, "Online")
        };

        // Clear node visuals down to the static frame (children[0]).
        for (int i = ClusterModelGroup.Children.Count - 1; i >= 1; i--)
        {
            if (ClusterModelGroup.Children[i] is System.Windows.Media.Media3D.GeometryModel3D)
                ClusterModelGroup.Children.RemoveAt(i);
        }
    }

    private void RenderNodes(IReadOnlyList<ClusterNodeResponse> nodes)
    {
        // Update DataGrid
        NodesGrid.ItemsSource = nodes.Select(n => new ClusterNodeInfo(
            Name: n.Hostname,
            Role: n.Role == ClusterRole.Leader ? "Leader" : "Follower",
            ClientCount: n.CurrentClientCount,
            CpuPercent: (int)Math.Round(n.CpuUsagePercent),
            MemPercent: (int)Math.Round(n.MemoryUsagePercent),
            Health: HealthFromStatus(n.Status))).ToList();

        // Rebuild 3D node markers — clear any existing GeometryModel3D first
        // so we don't pile them up across refreshes.
        for (int i = ClusterModelGroup.Children.Count - 1; i >= 1; i--)
        {
            if (ClusterModelGroup.Children[i] is System.Windows.Media.Media3D.GeometryModel3D)
                ClusterModelGroup.Children.RemoveAt(i);
        }

        double y = 0.175;
        foreach (var n in nodes.Take(8))
        {
            var color = ColorFromStatus(n.Status);
            var marker = Viewport3DHelper.CreateServerNode(0, y, 0, color);
            ClusterModelGroup.Children.Add(marker);
            y += 0.35;
        }
    }

    private static string HealthFromStatus(NodeStatus s) => s switch
    {
        NodeStatus.Online => "Healthy",
        NodeStatus.Suspect => "Warning",
        NodeStatus.Joining => "Joining",
        NodeStatus.Leaving => "Leaving",
        NodeStatus.Offline => "Error",
        _ => s.ToString()
    };

    private static Color ColorFromStatus(NodeStatus s) => s switch
    {
        NodeStatus.Online => Color.FromRgb(63, 185, 80),
        NodeStatus.Suspect => Color.FromRgb(210, 153, 34),
        NodeStatus.Joining => Color.FromRgb(88, 166, 255),
        NodeStatus.Leaving => Color.FromRgb(176, 184, 196),
        NodeStatus.Offline => Color.FromRgb(248, 81, 73),
        _ => Color.FromRgb(176, 184, 196)
    };

    public record ClusterNodeInfo(
        string Name,
        string Role,
        int ClientCount,
        int CpuPercent,
        int MemPercent,
        string Health);
}
