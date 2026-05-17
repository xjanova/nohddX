using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NohddX.Ui.ViewModels;

public class MainViewModel : ObservableObject
{
    private string _selectedView = "Dashboard";
    private string _serverStatus = "Connecting...";
    private int _connectedClients;
    private string _uptime = "00:00:00";
    private string _clusterStatus = "Standalone";
    private string _serverAddress = "http://localhost:8080";

    public string SelectedView
    {
        get => _selectedView;
        set => SetProperty(ref _selectedView, value);
    }

    public string ServerStatus
    {
        get => _serverStatus;
        set => SetProperty(ref _serverStatus, value);
    }

    public int ConnectedClients
    {
        get => _connectedClients;
        set => SetProperty(ref _connectedClients, value);
    }

    public string Uptime
    {
        get => _uptime;
        set => SetProperty(ref _uptime, value);
    }

    public string ClusterStatus
    {
        get => _clusterStatus;
        set => SetProperty(ref _clusterStatus, value);
    }

    public string ServerAddress
    {
        get => _serverAddress;
        set => SetProperty(ref _serverAddress, value);
    }

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        NavigateCommand = new RelayCommand<string>(Navigate);
    }

    public void Navigate(string? view)
    {
        if (view is not null)
            SelectedView = view;
    }
}
