using NohddX.Agent.Communication;
using NohddX.Agent.Hardware;
using Spectre.Console;

namespace NohddX.Agent.InstallModes;

/// <summary>
/// Diskless USB+Network mode: the local USB stick remains the boot
/// device but the runtime root filesystem is fetched over the
/// network from the NoHddX server (typically via iSCSI / NBD).
/// On non-Linux this is a no-op simulation.
/// </summary>
public class DisklessUsbMode : IInstallMode
{
    public string Name => "Diskless boot via USB+Network";

    public async Task ExecuteAsync(HardwareInfo hardware, AgentApiClient client, string agentId, CancellationToken ct)
    {
        await client.SendStatusAsync(agentId, "diskless-init", 0, ct);

        InstallInstructions? instructions = null;
        await AnsiConsole.Status()
            .StartAsync("[cyan]Requesting diskless configuration...[/]", async _ =>
            {
                instructions = await client.RequestInstallAsync(agentId, "Diskless", ct);
            });

        if (instructions == null)
        {
            AnsiConsole.MarkupLine("[red]Server did not return diskless instructions.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Backing image:[/] {Markup.Escape(instructions.ImageUrl)}");
        AnsiConsole.MarkupLine($"[grey]Mount target:[/] {Markup.Escape(instructions.TargetDisk)}");

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
                var connect = ctx.AddTask("[green]Connecting to network share[/]");
                var mount = ctx.AddTask("[green]Mounting remote root[/]");
                var prep = ctx.AddTask("[green]Preparing pivot[/]");

                while (!connect.IsFinished) { connect.Increment(20); await Task.Delay(100, ct); }
                await client.SendStatusAsync(agentId, "diskless-connected", 33, ct);

                while (!mount.IsFinished) { mount.Increment(20); await Task.Delay(100, ct); }
                await client.SendStatusAsync(agentId, "diskless-mounted", 66, ct);

                while (!prep.IsFinished) { prep.Increment(20); await Task.Delay(100, ct); }
                await client.SendStatusAsync(agentId, "diskless-ready", 100, ct);
            });

        AnsiConsole.MarkupLine("[green]Diskless environment is ready. Pivot would happen here on a real system.[/]");
    }
}
