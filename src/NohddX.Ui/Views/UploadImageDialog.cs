using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NohddX.Core.Models;
using NohddX.Ui.Services;

namespace NohddX.Ui.Views;

/// <summary>
/// Modal for "Upload Image" — picks a local VHD, asks for name/os/version,
/// then streams the file to <c>POST /api/images/upload</c>. Shows real-byte
/// progress so multi-GB uploads don't look frozen. Cancel button aborts the
/// transfer via CancellationTokenSource.
/// </summary>
public sealed class UploadImageDialog : Window
{
    private readonly TextBox _filePathBox;
    private readonly TextBox _nameBox;
    private readonly ComboBox _osCombo;
    private readonly TextBox _versionBox;
    private readonly CheckBox _defaultBox;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _uploadBtn;
    private readonly Button _cancelBtn;
    private CancellationTokenSource? _cts;

    public UploadImageDialog()
    {
        Title = "Upload Boot Image";
        Width = 560;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(33, 38, 45));
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237));
        FontFamily = new FontFamily("Segoe UI");

        var grid = new Grid { Margin = new Thickness(16) };
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── File picker ────────────────────────────────
        grid.Children.Add(Label("Local file", 0));
        var filePicker = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(filePicker, 1);
        _filePathBox = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas"),
            IsReadOnly = true,
        };
        var browse = MakeButton("Browse");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select VHD or raw image",
                Filter = "Disk images (*.vhd;*.vhdx;*.img;*.raw)|*.vhd;*.vhdx;*.img;*.raw|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true)
            {
                _filePathBox.Text = dlg.FileName;
                if (string.IsNullOrEmpty(_nameBox.Text))
                    _nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        };
        DockPanel.SetDock(browse, Dock.Right);
        filePicker.Children.Add(browse);
        filePicker.Children.Add(_filePathBox);
        grid.Children.Add(filePicker);

        // ── Name ───────────────────────────────────────
        grid.Children.Add(Label("Image name", 2));
        _nameBox = TextBox(3);
        grid.Children.Add(_nameBox);

        // ── OS Type + Version row ──────────────────────
        var osRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(osRow, 4);
        osRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        osRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        osRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        osRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        osRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var osStack = new StackPanel();
        osStack.Children.Add(Label("OS type", 0, isStack: true));
        _osCombo = new ComboBox
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
        };
        foreach (OsType t in Enum.GetValues(typeof(OsType)))
            _osCombo.Items.Add(t);
        _osCombo.SelectedIndex = (int)OsType.Windows;
        osStack.Children.Add(_osCombo);
        Grid.SetColumn(osStack, 0);
        osRow.Children.Add(osStack);

        var vStack = new StackPanel();
        vStack.Children.Add(Label("Version", 0, isStack: true));
        _versionBox = new TextBox
        {
            Text = "1.0",
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas"),
        };
        vStack.Children.Add(_versionBox);
        Grid.SetColumn(vStack, 2);
        osRow.Children.Add(vStack);

        _defaultBox = new CheckBox
        {
            Content = "Default",
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetColumn(_defaultBox, 4);
        osRow.Children.Add(_defaultBox);
        grid.Children.Add(osRow);

        // ── Progress + status ──────────────────────────
        _progress = new ProgressBar
        {
            Height = 6,
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(56, 139, 253)),
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Minimum = 0,
            Maximum = 1,
        };
        Grid.SetRow(_progress, 5);
        grid.Children.Add(_progress);

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(176, 184, 196)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(_status, 6);
        grid.Children.Add(_status);

        // ── Buttons ────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttons, 7);

        _cancelBtn = MakeButton("Cancel");
        _cancelBtn.Click += (_, _) =>
        {
            if (_cts is not null) { _cts.Cancel(); _status.Text = "Cancelling..."; }
            else { DialogResult = false; Close(); }
        };
        buttons.Children.Add(_cancelBtn);

        _uploadBtn = MakeButton("Upload");
        _uploadBtn.Margin = new Thickness(8, 0, 0, 0);
        _uploadBtn.Background = new SolidColorBrush(Color.FromRgb(56, 139, 253));
        _uploadBtn.Click += async (_, _) => await DoUploadAsync();
        buttons.Children.Add(_uploadBtn);

        grid.Children.Add(buttons);
        Content = grid;
    }

    private async Task DoUploadAsync()
    {
        var path = _filePathBox.Text;
        var name = _nameBox.Text.Trim();
        var version = string.IsNullOrWhiteSpace(_versionBox.Text) ? "1.0" : _versionBox.Text.Trim();
        var osType = (OsType)(_osCombo.SelectedItem ?? OsType.Custom);
        var isDefault = _defaultBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("Pick a file first.", "Upload", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Image name is required.", "Upload", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long fileSize = new FileInfo(path).Length;
        _progress.Maximum = fileSize;
        _uploadBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();

        var progress = new Progress<long>(bytes =>
        {
            _progress.Value = bytes;
            _status.Text = $"{FormatSize(bytes)} / {FormatSize(fileSize)}  ({bytes * 100.0 / Math.Max(1, fileSize):0.0}%)";
        });

        try
        {
            await App.ApiClient.UploadImageAsync(path, name, osType, version, isDefault, progress, _cts.Token);
            _status.Text = "Done.";
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            _uploadBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Failed: {ex.Message}";
            _uploadBtn.IsEnabled = true;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{bytes / 1024.0:0.0} KB";
    }

    private static TextBlock Label(string text, int row, bool isStack = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(176, 184, 196)),
        };
        if (!isStack) Grid.SetRow(tb, row);
        return tb;
    }

    private static TextBox TextBox(int row)
    {
        var tb = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas"),
        };
        Grid.SetRow(tb, row);
        return tb;
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(16, 6, 16, 6),
        Background = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
        BorderThickness = new Thickness(1),
    };
}
