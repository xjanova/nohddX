using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Boot.Http;

/// <summary>
/// Generates iPXE boot scripts for clients to boot from iSCSI targets.
/// Implements <see cref="IIpxeScriptGenerator"/> with per-client script generation
/// based on client/image/node assignments.
/// </summary>
public class IpxeScriptGenerator : IIpxeScriptGenerator
{
    private readonly NohddxOptions _options;
    private readonly ILogger<IpxeScriptGenerator> _logger;

    public IpxeScriptGenerator(
        IOptions<NohddxOptions> options,
        ILogger<IpxeScriptGenerator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates a full iPXE boot script for a specific client, image, and cluster node.
    /// The script configures iSCSI SAN boot with retry logic.
    /// </summary>
    /// <param name="client">The client machine that will execute this script.</param>
    /// <param name="image">The boot image assigned to the client.</param>
    /// <param name="node">The cluster node hosting the iSCSI target.</param>
    /// <returns>A complete iPXE script as a string.</returns>
    public string GenerateBootScript(ClientMachine client, BootImage image, ClusterNode node)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (node == null) throw new ArgumentNullException(nameof(node));

        var serverIp = node.IpAddress;
        var iscsiPort = node.IscsiPort > 0 ? node.IscsiPort : _options.Iscsi.Port;
        var iqn = $"{_options.Iscsi.IqnPrefix}:{client.Id}";

        _logger.LogDebug(
            "Generating boot script for client {Mac} -> image {Image} on node {Node}",
            client.MacAddress, image.Name, node.Hostname);

        return BuildIscsiBootScript(serverIp, iscsiPort, iqn, client, image);
    }

    /// <summary>
    /// Generates a discovery/registration script for unknown or unregistered clients.
    /// This script displays a message and the client's MAC address so an admin can register it.
    /// </summary>
    /// <param name="serverIp">The NohddX server IP address for the registration URL.</param>
    /// <returns>An iPXE script that informs the user to register the client.</returns>
    public string GenerateDiscoveryScript(string serverIp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!ipxe");
        sb.AppendLine("# NoHddX - Client not registered");
        sb.AppendLine();
        sb.AppendLine("echo ========================================");
        sb.AppendLine("echo   NoHddX Diskless Boot System");
        sb.AppendLine("echo   Client MAC: ${mac}");
        sb.AppendLine("echo   ERROR: Client not registered");
        sb.AppendLine("echo   Please register this client in NoHddX");

        if (!string.IsNullOrEmpty(serverIp))
        {
            sb.AppendLine($"echo   Server: http://{serverIp}");
        }

        sb.AppendLine("echo ========================================");
        sb.AppendLine("sleep 10");
        sb.AppendLine("exit");
        return sb.ToString();
    }

    private string BuildIscsiBootScript(string serverIp, int port, string iqn, ClientMachine client, BootImage image)
    {
        var clientLabel = client.Hostname ?? client.MacAddress;

        var sb = new StringBuilder();
        sb.AppendLine("#!ipxe");
        sb.AppendLine($"# NoHddX Boot Script for {clientLabel}");
        sb.AppendLine($"# Image: {image.Name} ({image.OsType})");
        sb.AppendLine();
        sb.AppendLine("dhcp");
        sb.AppendLine();
        sb.AppendLine($"set initiator-iqn {_options.Iscsi.IqnPrefix}:initiator:{client.Id}");
        sb.AppendLine($"sanhook --drive 0x80 iscsi:{serverIp}:{port}:0:{iqn}");
        sb.AppendLine();
        sb.AppendLine("sanboot --no-describe --drive 0x80 || goto retry");
        sb.AppendLine();
        sb.AppendLine(":retry");
        sb.AppendLine("echo Boot failed, retrying in 5 seconds...");
        sb.AppendLine("sleep 5");
        sb.AppendLine($"sanhook --drive 0x80 iscsi:{serverIp}:{port}:0:{iqn}");
        sb.AppendLine("sanboot --no-describe --drive 0x80 || goto retry");
        return sb.ToString();
    }
}
