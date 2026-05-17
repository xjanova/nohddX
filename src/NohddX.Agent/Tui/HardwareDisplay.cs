using NohddX.Agent.Hardware;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NohddX.Agent.Tui;

/// <summary>
/// Renders a <see cref="HardwareInfo"/> snapshot as a stack of
/// Spectre.Console tables wrapped in a single rounded panel.
/// </summary>
public static class HardwareDisplay
{
    public static void Show(HardwareInfo hw)
    {
        var panel = new Panel(BuildBody(hw))
        {
            Header = new PanelHeader("[bold cyan] Hardware Details [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(panel);
    }

    private static IRenderable BuildBody(HardwareInfo hw)
    {
        var summary = new Table().NoBorder().HideHeaders();
        summary.AddColumn(new TableColumn("").NoWrap());
        summary.AddColumn(new TableColumn(""));

        summary.AddRow("[grey]Hostname[/]", Markup.Escape(hw.Hostname));
        summary.AddRow("[grey]Manufacturer[/]", Markup.Escape($"{hw.System.Manufacturer} {hw.System.Model}"));
        summary.AddRow("[grey]Serial[/]", Markup.Escape(hw.System.SerialNumber));
        summary.AddRow("[grey]BIOS[/]", Markup.Escape(hw.System.BiosVersion));
        summary.AddRow("[grey]CPU[/]",
            Markup.Escape($"{hw.Cpu.Model} ({hw.Cpu.PhysicalCores}c/{hw.Cpu.LogicalCores}t @ {hw.Cpu.SpeedGhz:F2} GHz)"));
        summary.AddRow("[grey]Architecture[/]", Markup.Escape(hw.Cpu.Architecture));
        summary.AddRow("[grey]RAM[/]", FormatBytes(hw.Memory.TotalBytes));
        summary.AddRow("[grey]RAM Available[/]", FormatBytes(hw.Memory.AvailableBytes));
        summary.AddRow("[grey]Boot Mode[/]", Markup.Escape($"{hw.Boot.Mode} (Secure Boot: {hw.Boot.SecureBoot})"));

        var diskTable = new Table()
            .Title("[bold]Disks[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Device")
            .AddColumn("Model")
            .AddColumn("Size")
            .AddColumn("Type")
            .AddColumn("SMART");

        if (hw.Disks.Count == 0)
        {
            diskTable.AddRow("-", "-", "-", "-", "-");
        }
        else
        {
            foreach (var d in hw.Disks)
            {
                diskTable.AddRow(
                    Markup.Escape(d.Device),
                    Markup.Escape(d.Model),
                    FormatBytes(d.SizeBytes),
                    Markup.Escape(d.Type),
                    Markup.Escape(d.SmartHealth ?? "-"));
            }
        }

        var netTable = new Table()
            .Title("[bold]Network Interfaces[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Interface")
            .AddColumn("MAC")
            .AddColumn("IP")
            .AddColumn("Speed")
            .AddColumn("Up");

        if (hw.Networks.Count == 0)
        {
            netTable.AddRow("-", "-", "-", "-", "-");
        }
        else
        {
            foreach (var n in hw.Networks)
            {
                netTable.AddRow(
                    Markup.Escape(n.Interface),
                    Markup.Escape(n.MacAddress),
                    Markup.Escape(n.IpAddress ?? "-"),
                    n.SpeedMbps > 0 ? $"{n.SpeedMbps} Mbps" : "-",
                    n.IsConnected ? "[green]yes[/]" : "[red]no[/]");
            }
        }

        var gpuTable = new Table()
            .Title("[bold]GPUs[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Vendor")
            .AddColumn("Model");

        if (hw.Gpus.Count == 0)
        {
            gpuTable.AddRow("-", "-");
        }
        else
        {
            foreach (var g in hw.Gpus)
            {
                gpuTable.AddRow(
                    Markup.Escape(g.Vendor),
                    Markup.Escape(g.Model));
            }
        }

        var grid = new Grid().AddColumn();
        grid.AddRow(summary);
        grid.AddRow(diskTable);
        grid.AddRow(netTable);
        grid.AddRow(gpuTable);
        return grid;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0";
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        const double TB = GB * 1024;

        if (bytes >= TB) return $"{bytes / TB:F2} TB";
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F1} MB";
        if (bytes >= KB) return $"{bytes / KB:F0} KB";
        return $"{bytes} B";
    }
}
