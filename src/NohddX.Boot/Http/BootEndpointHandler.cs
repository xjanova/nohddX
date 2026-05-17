using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;

namespace NohddX.Boot.Http;

/// <summary>
/// Handles iPXE boot script requests from PXE clients.
/// This is a helper class that the API layer (e.g., ASP.NET controller) delegates to.
/// It looks up the client by MAC address, resolves the boot assignment, and generates
/// the appropriate iPXE script.
/// </summary>
public class BootEndpointHandler
{
    private readonly IIpxeScriptGenerator _scriptGenerator;
    private readonly IClientRepository _clientRepo;
    private readonly IBootAssignmentRepository _assignmentRepo;
    private readonly IImageRepository _imageRepo;
    private readonly IClusterNodeRepository _nodeRepo;
    private readonly NohddxOptions _options;
    private readonly ILogger<BootEndpointHandler> _logger;

    // Matches MAC addresses in common formats: XX:XX:XX:XX:XX:XX, XX-XX-XX-XX-XX-XX, XXXXXXXXXXXX
    private static readonly Regex MacAddressPattern = new(
        @"^([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}$|^[0-9A-Fa-f]{12}$",
        RegexOptions.Compiled);

    public BootEndpointHandler(
        IIpxeScriptGenerator scriptGenerator,
        IClientRepository clientRepo,
        IBootAssignmentRepository assignmentRepo,
        IImageRepository imageRepo,
        IClusterNodeRepository nodeRepo,
        IOptions<NohddxOptions> options,
        ILogger<BootEndpointHandler> logger)
    {
        _scriptGenerator = scriptGenerator;
        _clientRepo = clientRepo;
        _assignmentRepo = assignmentRepo;
        _imageRepo = imageRepo;
        _nodeRepo = nodeRepo;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Handles a boot script request for the given MAC address.
    /// Looks up the client, assignment, image, and node, then generates the iPXE script.
    /// </summary>
    /// <param name="macAddress">The requesting client's MAC address in any common format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (iPXE script content, content type).</returns>
    public async Task<(string Script, string ContentType)> HandleBootRequestAsync(
        string macAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            _logger.LogWarning("Boot request with empty MAC address");
            return (GetDiscoveryScript(), "text/plain");
        }

        var normalizedMac = NormalizeMac(macAddress);

        if (!MacAddressPattern.IsMatch(normalizedMac.Replace("-", ":")))
        {
            _logger.LogWarning("Boot request with invalid MAC format: {Mac}", macAddress);
            return (GetDiscoveryScript(), "text/plain");
        }

        // Look up client by MAC address
        var client = await _clientRepo.GetByMacAddressAsync(normalizedMac, ct);
        if (client == null)
        {
            _logger.LogWarning("Unknown client MAC: {Mac}, returning discovery script", normalizedMac);
            return (GetDiscoveryScript(), "text/plain");
        }

        // Get active boot assignment
        var assignment = await _assignmentRepo.GetByClientIdAsync(client.Id, ct);
        if (assignment == null || !assignment.IsActive)
        {
            _logger.LogWarning("No active boot assignment for client {Mac} ({Id})", normalizedMac, client.Id);
            return (GetDiscoveryScript(), "text/plain");
        }

        // Get the boot image
        var image = await _imageRepo.GetByIdAsync(assignment.ImageId, ct);
        if (image == null)
        {
            _logger.LogWarning("Boot image {ImageId} not found for client {Mac}", assignment.ImageId, normalizedMac);
            return (GetDiscoveryScript(), "text/plain");
        }

        // Get the cluster node that will serve this client
        var node = client.AssignedNodeId.HasValue
            ? await _nodeRepo.GetByIdAsync(client.AssignedNodeId.Value, ct)
            : null;

        if (node == null)
        {
            _logger.LogWarning("No assigned node for client {Mac}, using local server", normalizedMac);
            // Build a temporary node representing the local server
            node = new Core.Models.ClusterNode
            {
                IpAddress = _options.DhcpProxy.NextServerIp ?? GetLocalIpAddress(),
                IscsiPort = _options.Iscsi.Port,
                Hostname = Environment.MachineName,
            };
        }

        _logger.LogInformation(
            "Generating boot script for {Mac}: image={Image}, node={Node}",
            normalizedMac, image.Name, node.Hostname);

        var script = _scriptGenerator.GenerateBootScript(client, image, node);
        return (script, "text/plain");
    }

    private string GetDiscoveryScript()
    {
        var serverIp = _options.DhcpProxy.NextServerIp ?? GetLocalIpAddress();
        return _scriptGenerator.GenerateDiscoveryScript(serverIp);
    }

    /// <summary>
    /// Normalizes a MAC address to the XX-XX-XX-XX-XX-XX format.
    /// Accepts colons, hyphens, dots, or no separators.
    /// </summary>
    public static string NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return string.Empty;

        // Strip all common separators
        var clean = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpperInvariant();

        if (clean.Length != 12)
            return mac.ToUpperInvariant(); // Return as-is if not 12 hex chars

        // Validate all characters are hex
        if (!clean.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
            return mac.ToUpperInvariant();

        // Format as XX-XX-XX-XX-XX-XX
        return string.Join("-", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is System.Net.IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch
        {
            // Fallback below
        }

        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(ip))
                return ip.ToString();
        }

        return "127.0.0.1";
    }
}
