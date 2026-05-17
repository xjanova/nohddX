using System.Windows.Controls;
using System.Windows.Threading;
using NohddX.Api.DTOs;
using NohddX.Core.Models;

namespace NohddX.Ui.Views;

public partial class StorageView : UserControl
{
    private readonly DispatcherTimer _refreshTimer;

    public StorageView()
    {
        InitializeComponent();

        Loaded += async (_, _) => await RefreshAsync();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    private async Task RefreshAsync()
    {
        // Pools come from a real API (sums VHD base + overlay paths)
        var health = await App.ApiClient.GetStorageHealthAsync();
        StoragePoolsList.ItemsSource = (health?.Pools ?? Array.Empty<StoragePoolResponse>())
            .Select(ToPool).ToList();

        // Real disk enumeration from the server (DriveInfo-based; portable).
        var disks = await App.ApiClient.GetDisksAsync();
        if (disks.Count == 0)
        {
            DiskHealthGrid.ItemsSource = new[]
            {
                new DiskHealth("(none)", "Server reported no drives", "—", "—", "—", "Good")
            };
        }
        else
        {
            DiskHealthGrid.ItemsSource = disks.Select(d => new DiskHealth(
                Device: d.Device,
                Model: $"{d.DriveType} ({d.DriveFormat})" +
                       (string.IsNullOrEmpty(d.VolumeLabel) ? "" : $" — {d.VolumeLabel}"),
                Size: FormatSize(d.TotalBytes),
                // Temperature column reused for usage% until SMART exists.
                Temperature: $"{d.UsagePercent:0.0}%",
                Pool: "—",
                Health: d.Health)).ToList();
        }

        // Image storage breakdown — derive from /api/images. We don't have a
        // proper /api/storage/images endpoint that decomposes base/overlay/snapshot
        // bytes, so we show the base size and assignment/snapshot counts. The
        // ProgressBar reflects the relative size of each image among the fleet.
        var images = await App.ApiClient.GetImagesAsync();
        if (images.Count == 0)
        {
            ImageStorageGrid.ItemsSource = Array.Empty<ImageStorage>();
            return;
        }

        long maxBytes = images.Max(i => i.SizeBytes);
        ImageStorageGrid.ItemsSource = images.Select(i => new ImageStorage(
            ImageName: i.Name,
            BaseSize: FormatSize(i.SizeBytes),
            OverlaySize: $"{i.AssignmentCount} clients",
            SnapshotSize: $"{i.SnapshotCount} snapshots",
            TotalSize: FormatSize(i.SizeBytes),
            SizePercent: maxBytes > 0 ? i.SizeBytes * 100.0 / maxBytes : 0)).ToList();
    }

    private static StoragePool ToPool(StoragePoolResponse p)
    {
        double usagePct = p.TotalBytes > 0 ? p.UsedBytes * 100.0 / p.TotalBytes : 0;
        return new StoragePool(
            Name: p.Name,
            RaidLevel: p.RaidLevel ?? "—",
            HealthStatus: p.RaidStatus.ToString(),
            UsedGb: p.UsedBytes / (double)(1L << 30),
            TotalGb: p.TotalBytes / (double)(1L << 30),
            UsedFormatted: FormatSize(p.UsedBytes),
            TotalFormatted: FormatSize(p.TotalBytes),
            FreeFormatted: FormatSize(p.FreeBytes),
            UsagePercent: Math.Round(usagePct, 1),
            IsHighUsage: usagePct >= 75 && usagePct < 90,
            IsCriticalUsage: usagePct >= 90);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.0} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{bytes / 1024.0:0.0} KB";
    }

    public record StoragePool(
        string Name,
        string RaidLevel,
        string HealthStatus,
        double UsedGb,
        double TotalGb,
        string UsedFormatted,
        string TotalFormatted,
        string FreeFormatted,
        double UsagePercent,
        bool IsHighUsage,
        bool IsCriticalUsage);

    public record DiskHealth(
        string Device,
        string Model,
        string Size,
        string Temperature,
        string Pool,
        string Health);

    public record ImageStorage(
        string ImageName,
        string BaseSize,
        string OverlaySize,
        string SnapshotSize,
        string TotalSize,
        double SizePercent);
}
