using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NohddX.Api.DTOs;
using NohddX.Core.Models;

namespace NohddX.Ui.Views;

public partial class ImagesView : UserControl
{
    public ImagesView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var images = await App.ApiClient.GetImagesAsync();
            ImagesPanel.ItemsSource = images.Select(ToCard).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load images failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UploadImageDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            await RefreshAsync();
        }
    }

    // ── Mapping ───────────────────────────────────────

    // Same SVG paths the original mock used so the cards still look like the
    // OS they represent. Picked by OsType.
    private const string WindowsIcon = "M0,3.5 L10,2 L10,11 L0,11 Z M11,1.8 L22,0 L22,11 L11,11 Z M0,12 L10,12 L10,21 L0,19.5 Z M11,12 L22,12 L22,23 L11,21.2 Z";
    private const string LinuxIcon = "M12,2C9,2 7,5 7,9C7,11 7.5,12 6,14C4.5,16 4,17 4,19C4,21 5.5,22 8,22L16,22C18.5,22 20,21 20,19C20,17 19.5,16 18,14C16.5,12 17,11 17,9C17,5 15,2 12,2Z M10,10A1,1 0 1,0 10,12A1,1 0 1,0 10,10Z M14,10A1,1 0 1,0 14,12A1,1 0 1,0 14,10Z M10,16L14,16C14,17.1 13.1,18 12,18C10.9,18 10,17.1 10,16Z";
    private const string CustomIcon = "M12,8A4,4 0 1,0 16,12A4,4 0 0,0 12,8Z M19.4,13C19.4,12.7 19.5,12.3 19.5,12C19.5,11.7 19.4,11.3 19.4,11L21.5,9.4L19.5,5.6L17,6.8C16.4,6.3 15.7,5.9 15,5.6L14.6,3L9.4,3L9,5.6C8.3,5.9 7.6,6.3 7,6.8L4.5,5.6L2.5,9.4L4.6,11C4.6,11.3 4.5,11.7 4.5,12C4.5,12.3 4.6,12.7 4.6,13L2.5,14.6L4.5,18.4L7,17.2C7.6,17.7 8.3,18.1 9,18.4L9.4,21L14.6,21L15,18.4C15.7,18.1 16.4,17.7 17,17.2L19.5,18.4L21.5,14.6L19.4,13Z";

    private static ImageInfo ToCard(ImageResponse r)
    {
        var (iconPath, iconColor) = r.OsType switch
        {
            OsType.Windows => (WindowsIcon, new SolidColorBrush(Color.FromRgb(88, 166, 255))),
            OsType.Linux => (LinuxIcon, new SolidColorBrush(Color.FromRgb(63, 185, 80))),
            _ => (CustomIcon, new SolidColorBrush(Color.FromRgb(188, 140, 255)))
        };

        return new ImageInfo(
            r.Name,
            r.OsType.ToString(),
            r.Version,
            FormatSize(r.SizeBytes),
            r.AssignmentCount,
            r.Status.ToString(),
            iconPath,
            iconColor);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{bytes / 1024.0:0.0} KB";
    }

    public record ImageInfo(
        string Name,
        string OsType,
        string Version,
        string SizeFormatted,
        int UsageCount,
        string StatusText,
        string OsIconPath,
        Brush OsIconColor);
}
