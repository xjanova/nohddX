using Spectre.Console;

namespace NohddX.Agent.Tui;

/// <summary>
/// Top-level Spectre.Console menu shown to the operator after
/// hardware detection and server discovery have completed.
/// </summary>
public class MainMenu
{
    private static readonly string[] Choices =
    {
        "1. Install OS to local HDD (Persistent)",
        "2. Diskless boot via USB+Network",
        "3. NoHddX Network Boot (iSCSI from server)",
        "4. Show hardware details",
        "5. Run diagnostics",
        "6. Reboot",
        "7. Shutdown"
    };

    public InstallMode Show()
    {
        AnsiConsole.WriteLine();
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold cyan]Select Boot Mode:[/]")
                .PageSize(10)
                .HighlightStyle(new Style(foreground: Color.Cyan1, decoration: Decoration.Bold))
                .AddChoices(Choices));

        return choice.Length > 0 ? choice[0] switch
        {
            '1' => InstallMode.Persistent,
            '2' => InstallMode.DisklessUsb,
            '3' => InstallMode.NetworkBoot,
            '4' => InstallMode.ShowHardware,
            '5' => InstallMode.Diagnostics,
            '6' => InstallMode.Reboot,
            '7' => InstallMode.Shutdown,
            _ => InstallMode.Persistent
        } : InstallMode.Persistent;
    }
}

public enum InstallMode
{
    Persistent,
    DisklessUsb,
    NetworkBoot,
    ShowHardware,
    Diagnostics,
    Reboot,
    Shutdown
}
