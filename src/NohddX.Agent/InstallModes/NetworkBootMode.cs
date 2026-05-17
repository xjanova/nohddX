using NohddX.Agent.Communication;
using NohddX.Agent.Hardware;
using Spectre.Console;
// CommandRunner lives in NohddX.Agent.Hardware namespace; the using above brings it in.

namespace NohddX.Agent.InstallModes;

/// <summary>
/// NoHddX network boot mode: configure the BIOS/UEFI boot order so the
/// next reboot performs a PXE/iSCSI boot from the NoHddX server, then
/// reboots the machine. The actual order change is delegated to the
/// server-supplied script in <see cref="InstallInstructions.Metadata"/>.
/// </summary>
public class NetworkBootMode : IInstallMode
{
    public string Name => "Network boot (iSCSI/PXE next reboot)";

    public async Task ExecuteAsync(HardwareInfo hardware, AgentApiClient client, string agentId, CancellationToken ct)
    {
        await client.SendStatusAsync(agentId, "netboot-init", 0, ct);

        InstallInstructions? instructions = null;
        await AnsiConsole.Status()
            .StartAsync("[cyan]Requesting network boot configuration...[/]", async _ =>
            {
                instructions = await client.RequestInstallAsync(agentId, "NetworkBoot", ct);
            });

        if (instructions == null)
        {
            AnsiConsole.MarkupLine("[red]Server did not return network-boot instructions.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Boot script:[/] {Markup.Escape(instructions.ImageUrl)}");
        if (instructions.Metadata.TryGetValue("targetMac", out var mac))
            AnsiConsole.MarkupLine($"[grey]Target MAC:[/] {Markup.Escape(mac)}");

        if (!AnsiConsole.Confirm("Reboot now into network boot?", true))
        {
            AnsiConsole.MarkupLine("[grey]Reboot deferred. The agent will reboot on the next manual trigger.[/]");
            await client.SendStatusAsync(agentId, "netboot-deferred", 100, ct);
            return;
        }

        await client.SendStatusAsync(agentId, "netboot-rebooting", 100, ct);

        if (OperatingSystem.IsLinux())
        {
            await CommandRunner.RunAsync("shutdown", "-r now", 5000);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey](Reboot skipped on this platform.)[/]");
        }
    }
}
