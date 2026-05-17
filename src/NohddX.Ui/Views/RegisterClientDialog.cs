using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NohddX.Ui.Views;

/// <summary>
/// Minimal MAC + hostname prompt. Code-only dialog (no XAML) so we don't
/// have to wire another resource dictionary; uses the same dark palette as
/// the rest of the operator console for visual consistency.
/// </summary>
public sealed class RegisterClientDialog : Window
{
    private static readonly Regex MacPattern = new(
        @"^[0-9A-Fa-f]{2}([:\-]?[0-9A-Fa-f]{2}){5}$",
        RegexOptions.Compiled);

    private readonly TextBox _macBox;
    private readonly TextBox _hostBox;

    public string MacAddress { get; private set; } = "";
    public string? Hostname { get; private set; }

    public RegisterClientDialog()
    {
        Title = "Register Client";
        Width = 420;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(33, 38, 45));
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237));
        FontFamily = new FontFamily("Segoe UI");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(MakeLabel("MAC address", 0));
        _macBox = MakeTextBox("AA:BB:CC:DD:EE:FF", 1);
        grid.Children.Add(_macBox);

        grid.Children.Add(MakeLabel("Hostname (optional)", 2));
        _hostBox = MakeTextBox("", 3);
        grid.Children.Add(_hostBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttons, 5);

        var cancel = MakeButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(cancel);

        var ok = MakeButton("Register");
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.Background = new SolidColorBrush(Color.FromRgb(56, 139, 253));
        ok.Click += (_, _) =>
        {
            var mac = _macBox.Text.Trim();
            if (!MacPattern.IsMatch(mac))
            {
                MessageBox.Show("Enter a valid MAC address (e.g. AA:BB:CC:DD:EE:FF).",
                    "Invalid MAC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MacAddress = mac;
            Hostname = string.IsNullOrWhiteSpace(_hostBox.Text) ? null : _hostBox.Text.Trim();
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(ok);

        grid.Children.Add(buttons);
        Content = grid;
        _macBox.Focus();
    }

    private static TextBlock MakeLabel(string text, int row)
    {
        var tb = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(176, 184, 196))
        };
        Grid.SetRow(tb, row);
        return tb;
    }

    private static TextBox MakeTextBox(string placeholder, int row)
    {
        var tb = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas")
        };
        Grid.SetRow(tb, row);
        return tb;
    }

    private static Button MakeButton(string label)
    {
        return new Button
        {
            Content = label,
            Padding = new Thickness(16, 6, 16, 6),
            Background = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 68, 77)),
            BorderThickness = new Thickness(1)
        };
    }
}
