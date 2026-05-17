using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NohddX.Api.DTOs;

namespace NohddX.Ui.Views;

/// <summary>
/// Modal list-of-images picker used when assigning a boot image to a client.
/// Code-only to avoid adding more XAML files for trivial dialogs.
/// </summary>
public sealed class PickImageDialog : Window
{
    private readonly ListBox _list;

    public Guid? SelectedImageId { get; private set; }

    public PickImageDialog(IReadOnlyList<ImageResponse> images)
    {
        Title = "Pick Boot Image";
        Width = 460;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(33, 38, 45));
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237));
        FontFamily = new FontFamily("Segoe UI");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1)
        };
        foreach (var img in images)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{img.Name} — {img.OsType} {img.Version}   ({img.SizeBytes / (1024 * 1024)} MB)",
                Tag = img.Id,
                Padding = new Thickness(8, 6, 8, 6)
            });
        }
        if (images.Count > 0) _list.SelectedIndex = 0;
        Grid.SetRow(_list, 0);
        grid.Children.Add(_list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(buttons, 1);

        var cancel = MakeButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(cancel);

        var ok = MakeButton("Assign");
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.Background = new SolidColorBrush(Color.FromRgb(56, 139, 253));
        ok.Click += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem li && li.Tag is Guid id)
            {
                SelectedImageId = id;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Select an image first.", "No selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        buttons.Children.Add(ok);

        grid.Children.Add(buttons);
        Content = grid;
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(16, 6, 16, 6),
        Background = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
        BorderThickness = new Thickness(1)
    };
}
