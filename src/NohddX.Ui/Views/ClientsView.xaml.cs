using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NohddX.Api.DTOs;
using NohddX.Ui.Services;

namespace NohddX.Ui.Views;

public partial class ClientsView : UserControl
{
    private readonly ObservableCollection<ClientRow> _allClients = new();
    private bool _isLoading;

    public ClientsView()
    {
        InitializeComponent();

        // Live-update on SignalR push.
        App.Dashboard.ClientStatusChanged += OnClientStatusChanged;

        Loaded += async (_, _) => await RefreshAsync();
    }

    // ── Data load ─────────────────────────────────────

    public async Task RefreshAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            StatusText.Text = "Loading...";
            var clients = await App.ApiClient.GetClientsAsync();

            _allClients.Clear();
            foreach (var c in clients)
                _allClients.Add(ToRow(c));

            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static ClientRow ToRow(ClientResponse c)
    {
        var lastSeen = c.LastSeen is null
            ? "—"
            : c.Status == "Online" ? "Now" : c.LastSeen.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        return new ClientRow(
            Id: c.Id,
            Status: c.Status,
            MacAddress: c.MacAddress,
            Hostname: c.Hostname ?? "—",
            IpAddress: c.IpAddress ?? "—",
            Group: c.GroupName ?? "—",
            ImageName: c.ImageName ?? "—",
            ImageId: c.ImageId,
            LastSeen: lastSeen);
    }

    private void OnClientStatusChanged(Guid clientId, string status)
    {
        // Find the row and bump its status without a full reload. Falls back
        // to a full refresh if the client isn't in our list yet.
        for (int i = 0; i < _allClients.Count; i++)
        {
            if (_allClients[i].Id == clientId)
            {
                _allClients[i] = _allClients[i] with { Status = status };
                ApplyFilter();
                return;
            }
        }

        _ = RefreshAsync();
    }

    // ── Filtering / Search ─────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ClientsGrid is null || StatusText is null) return;

        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var filter = (FilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";

        var filtered = _allClients.Where(c =>
        {
            if (filter != "All" && !c.Status.Equals(filter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(search))
            {
                return c.MacAddress.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || c.Hostname.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || c.IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }).ToList();

        ClientsGrid.ItemsSource = filtered;

        var online = _allClients.Count(c => c.Status == "Online");
        var booting = _allClients.Count(c => c.Status == "Booting");
        StatusText.Text = $"{_allClients.Count} clients total, {online} online, {booting} booting  " +
                          $"(showing {filtered.Count})";
    }

    // ── Toolbar actions ───────────────────────────────

    private async void AddClient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RegisterClientDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await App.ApiClient.RegisterClientAsync(dlg.MacAddress, dlg.Hostname);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Register failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AssignImage_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row)
        {
            MessageBox.Show("Select a client first.", "Assign image", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<ImageResponse> images;
        try
        {
            images = await App.ApiClient.GetImagesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load images failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dlg = new PickImageDialog(images) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.SelectedImageId is null) return;

        try
        {
            await App.ApiClient.AssignImageAsync(row.Id, dlg.SelectedImageId.Value);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Assign failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Wake_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row) return;
        try
        {
            await App.ApiClient.WakeAsync(row.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Wake failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row) return;

        var confirm = MessageBox.Show(
            $"Reset {row.Hostname} ({row.MacAddress})? This discards the CoW overlay.",
            "Confirm reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await App.ApiClient.ResetAsync(row.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reset failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row) return;

        var confirm = MessageBox.Show(
            $"Remove {row.Hostname} ({row.MacAddress})? This deletes the registration.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await App.ApiClient.DeleteClientAsync(row.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Row model ─────────────────────────────────────

    public record ClientRow(
        Guid Id,
        string Status,
        string MacAddress,
        string Hostname,
        string IpAddress,
        string Group,
        string ImageName,
        Guid? ImageId,
        string LastSeen);
}
