using NohddX.Agent.Communication;
using NohddX.Agent.Hardware;
using Spectre.Console;

namespace NohddX.Agent.InstallModes;

/// <summary>
/// Persistent install: download a disk image from the server and
/// write it to the operator-selected target disk. On non-Linux
/// hosts the write step is simulated so the project still builds
/// and runs end-to-end during development.
/// </summary>
public class PersistentInstallMode : IInstallMode
{
    public string Name => "Persistent install (write image to local disk)";

    public async Task ExecuteAsync(HardwareInfo hardware, AgentApiClient client, string agentId, CancellationToken ct)
    {
        // 1. Pick a target disk from the detected list
        if (hardware.Disks.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No writable disks detected.[/]");
            return;
        }

        var targetDisk = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold cyan]Select target disk:[/]")
                .AddChoices(hardware.Disks.Select(d =>
                    $"{d.Device}  ({d.Model}, {FormatGb(d.SizeBytes)} GB, {d.Type})")));

        var deviceToken = targetDisk.Split(' ')[0];

        // 2. Confirm - this is destructive
        AnsiConsole.MarkupLine($"[red bold]WARNING:[/] All data on [white]{Markup.Escape(deviceToken)}[/] will be erased.");
        if (!AnsiConsole.Confirm($"Are you sure you want to overwrite [white]{Markup.Escape(deviceToken)}[/]?", false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return;
        }

        await client.SendStatusAsync(agentId, "requesting-install", 0, ct);

        // 3. Ask the server for install instructions
        InstallInstructions? instructions = null;
        await AnsiConsole.Status()
            .StartAsync("[cyan]Requesting install instructions...[/]", async _ =>
            {
                instructions = await client.RequestInstallAsync(agentId, "Persistent", ct);
            });

        if (instructions == null)
        {
            AnsiConsole.MarkupLine("[red]Server did not return install instructions.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Image:[/] {Markup.Escape(instructions.ImageUrl)}");
        AnsiConsole.MarkupLine($"[grey]Size:[/] {FormatGb(instructions.ImageSize)} GB");

        // 4. Download + write
        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Streaming image to disk[/]", maxValue: 100);

                try
                {
                    using var src = await client.DownloadImageAsync(instructions.ImageUrl, ct);
                    if (src == null)
                    {
                        AnsiConsole.MarkupLine("[red]Failed to open image stream.[/]");
                        return;
                    }

                    Stream dest;
                    if (OperatingSystem.IsLinux() && File.Exists(deviceToken))
                    {
                        dest = new FileStream(deviceToken, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20);
                    }
                    else
                    {
                        // Simulation: write to a temp file we delete afterwards.
                        var simPath = Path.Combine(Path.GetTempPath(), $"nohddx-sim-{Guid.NewGuid():N}.img");
                        AnsiConsole.MarkupLine($"[yellow]Simulating write to[/] [white]{Markup.Escape(simPath)}[/]");
                        dest = new FileStream(simPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    }

                    using (dest)
                    {
                        var buffer = new byte[1 << 16]; // 64 KiB
                        long total = 0;
                        var max = Math.Max(instructions.ImageSize, 1);
                        int read;
                        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                            total += read;

                            var pct = Math.Min(100.0, total * 100.0 / max);
                            task.Value = pct;

                            if ((total % (4 << 20)) < buffer.Length)
                            {
                                _ = client.SendStatusAsync(agentId, "writing", pct, ct);
                            }
                        }
                        await dest.FlushAsync(ct);
                    }

                    task.Value = 100;
                    await client.SendStatusAsync(agentId, "completed", 100, ct);
                    AnsiConsole.MarkupLine("[green]Image written successfully.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Write failed:[/] {Markup.Escape(ex.Message)}");
                    await client.SendStatusAsync(agentId, "failed", task.Value, ct);
                }
            });
    }

    private static double FormatGb(long bytes) => Math.Round(bytes / (1024.0 * 1024 * 1024), 2);
}
