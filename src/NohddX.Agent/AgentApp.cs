using NohddX.Agent.Communication;
using NohddX.Agent.Discovery;
using NohddX.Agent.Hardware;
using NohddX.Agent.InstallModes;
using NohddX.Agent.Tui;
using Spectre.Console;

namespace NohddX.Agent;

/// <summary>
/// Main orchestrator for the NoHddX boot agent.
/// Coordinates hardware detection, server discovery, registration,
/// and execution of the user-selected install mode.
/// </summary>
public class AgentApp
{
    private readonly HardwareDetector _detector = new();
    private AgentConfig _config = new();
    private HardwareInfo? _hardware;
    private string? _serverUrl;
    private string? _agentId;
    private AgentApiClient? _apiClient;
    private AgentHttpServer? _httpServer;

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            // 1. Load configuration
            _config = AgentConfig.LoadFromStandardPaths();
            AnsiConsole.MarkupLine($"[grey]Config loaded. Agent listen port: {_config.AgentPort}[/]");
            AnsiConsole.WriteLine();

            // 2. Detect hardware (with progress)
            await DetectHardwareAsync();

            // 3. Discover server (mDNS/UDP + static fallback)
            await DiscoverServerAsync();

            // 4. Register with server (if discovered)
            if (_serverUrl != null)
            {
                _apiClient = new AgentApiClient(_serverUrl);
                await RegisterAsync();
                StartHttpServer();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No server available. Running in offline mode.[/]");
            }

            // 5. Main loop - menu + handler
            return await MainLoopAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private async Task DetectHardwareAsync()
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("[cyan]Detecting hardware...[/]", async ctx =>
            {
                ctx.Status("[cyan]Reading CPU info...[/]");
                await Task.Delay(150);
                ctx.Status("[cyan]Reading memory info...[/]");
                await Task.Delay(150);
                ctx.Status("[cyan]Enumerating disks...[/]");
                await Task.Delay(150);
                ctx.Status("[cyan]Enumerating network interfaces...[/]");
                await Task.Delay(150);
                ctx.Status("[cyan]Detecting GPUs...[/]");
                await Task.Delay(150);
                ctx.Status("[cyan]Reading firmware info...[/]");
                await Task.Delay(100);

                _hardware = await _detector.DetectAsync();
            });

        AnsiConsole.MarkupLine("[green]Hardware detection complete.[/]");
        AnsiConsole.WriteLine();
    }

    private async Task DiscoverServerAsync()
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("[cyan]Discovering NoHddX server...[/]", async ctx =>
            {
                var discovery = new ServerDiscovery(_config);
                _serverUrl = await discovery.DiscoverServerAsync();
            });

        if (_serverUrl != null)
        {
            AnsiConsole.MarkupLine($"[green]Server discovered:[/] [white]{Markup.Escape(_serverUrl)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Auto-discovery failed.[/]");
            if (AnsiConsole.Confirm("Enter server URL manually?", false))
            {
                var url = AnsiConsole.Ask<string>("[cyan]Server URL[/] (e.g. http://192.168.1.100:8080):");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _serverUrl = url.Trim();
                }
            }
        }
        AnsiConsole.WriteLine();
    }

    private async Task RegisterAsync()
    {
        if (_apiClient == null || _hardware == null) return;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("[cyan]Registering with server...[/]", async ctx =>
            {
                try
                {
                    var resp = await _apiClient.RegisterAsync(_hardware);
                    if (resp != null)
                    {
                        _agentId = resp.AgentId;
                        AnsiConsole.MarkupLine($"[green]Registered as agent:[/] [white]{Markup.Escape(resp.AgentId)}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]Registration returned no response.[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Registration failed:[/] {Markup.Escape(ex.Message)}");
                }
            });

        AnsiConsole.WriteLine();
    }

    private void StartHttpServer()
    {
        try
        {
            _httpServer = new AgentHttpServer(_config.AgentPort, _hardware, _agentId);
            _httpServer.CommandReceived += OnCommandReceived;
            _ = _httpServer.StartAsync(CancellationToken.None);
            AnsiConsole.MarkupLine($"[green]Agent HTTP server listening on port[/] [white]{_config.AgentPort}[/]");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not start agent HTTP server: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private void OnCommandReceived(object? sender, CommandReceivedEventArgs e)
    {
        AnsiConsole.MarkupLine($"[cyan]Server command received:[/] [white]{Markup.Escape(e.Command)}[/]");
    }

    private async Task<int> MainLoopAsync()
    {
        var menu = new MainMenu();

        while (true)
        {
            var mode = menu.Show();

            switch (mode)
            {
                case InstallMode.Persistent:
                case InstallMode.DisklessUsb:
                case InstallMode.NetworkBoot:
                    await ExecuteInstallModeAsync(mode);
                    break;

                case InstallMode.ShowHardware:
                    if (_hardware != null)
                        HardwareDisplay.Show(_hardware);
                    AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
                    try { Console.ReadKey(true); } catch { }
                    break;

                case InstallMode.Diagnostics:
                    await RunDiagnosticsAsync();
                    AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
                    try { Console.ReadKey(true); } catch { }
                    break;

                case InstallMode.Reboot:
                    AnsiConsole.MarkupLine("[yellow]Rebooting system...[/]");
                    await SystemPowerAsync(reboot: true);
                    return 0;

                case InstallMode.Shutdown:
                    AnsiConsole.MarkupLine("[yellow]Shutting down system...[/]");
                    await SystemPowerAsync(reboot: false);
                    return 0;
            }
        }
    }

    private async Task ExecuteInstallModeAsync(InstallMode mode)
    {
        if (_hardware == null)
        {
            AnsiConsole.MarkupLine("[red]Hardware not detected. Cannot proceed.[/]");
            return;
        }

        if (_apiClient == null || _agentId == null)
        {
            AnsiConsole.MarkupLine("[red]Not registered with a server. Cannot perform network install.[/]");
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            try { Console.ReadKey(true); } catch { }
            return;
        }

        IInstallMode handler = mode switch
        {
            InstallMode.Persistent => new PersistentInstallMode(),
            InstallMode.DisklessUsb => new DisklessUsbMode(),
            InstallMode.NetworkBoot => new NetworkBootMode(),
            _ => throw new InvalidOperationException("Unknown install mode")
        };

        AnsiConsole.MarkupLine($"[cyan]Running install mode:[/] [white]{handler.Name}[/]");

        try
        {
            await handler.ExecuteAsync(_hardware, _apiClient, _agentId, CancellationToken.None);
            AnsiConsole.MarkupLine("[green]Install mode completed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Install mode failed:[/] {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        try { Console.ReadKey(true); } catch { }
    }

    private async Task RunDiagnosticsAsync()
    {
        AnsiConsole.MarkupLine("[bold cyan]Running diagnostics...[/]");

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var hwTask = ctx.AddTask("[green]Hardware probe[/]");
                var netTask = ctx.AddTask("[green]Network connectivity[/]");
                var srvTask = ctx.AddTask("[green]Server reachability[/]");

                while (!hwTask.IsFinished)
                {
                    hwTask.Increment(20);
                    await Task.Delay(80);
                }
                while (!netTask.IsFinished)
                {
                    netTask.Increment(20);
                    await Task.Delay(80);
                }
                while (!srvTask.IsFinished)
                {
                    srvTask.Increment(20);
                    await Task.Delay(80);
                }
            });

        AnsiConsole.MarkupLine("[green]Diagnostics complete.[/]");
    }

    private async Task SystemPowerAsync(bool reboot)
    {
        if (OperatingSystem.IsLinux())
        {
            var args = reboot ? "-r now" : "-h now";
            await CommandRunner.RunAsync("shutdown", args, 5000);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey](Power action skipped on this platform.)[/]");
        }
    }
}
