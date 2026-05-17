using System.Windows;
using System.Windows.Controls;

namespace NohddX.Ui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        ServerUrlBox.Text = App.Settings.ServerUrl;
        ApiKeyBox.Text = App.Settings.AdminApiKey;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        // Test against the values currently in the textboxes WITHOUT saving
        // first, so the operator can validate before committing.
        var probe = new Services.AppSettings
        {
            ServerUrl = ServerUrlBox.Text.Trim(),
            AdminApiKey = ApiKeyBox.Text.Trim()
        };
        using var client = new Services.NohddxApiClient(probe);

        StatusLine.Text = "Testing...";
        var ok = await client.PingAsync();
        StatusLine.Text = ok ? "Server reachable." : "Cannot reach server.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ServerUrl = ServerUrlBox.Text.Trim();
        App.Settings.AdminApiKey = ApiKeyBox.Text.Trim();
        App.Settings.Save();
        StatusLine.Text = "Saved.";

        // Settings.Save() fires Changed -> ApiClient + DashboardConnection
        // rebuild themselves. Re-ping for instant feedback.
        await Task.Delay(150);
        var ok = await App.ApiClient.PingAsync();
        StatusLine.Text = ok ? "Saved. Server reachable." : "Saved. Server unreachable.";
    }
}
