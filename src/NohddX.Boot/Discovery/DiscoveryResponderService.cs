using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;

namespace NohddX.Boot.Discovery;

/// <summary>
/// Listens for UDP broadcast probes from the bootstrap agent and replies
/// with the server's own IP+port so agents can find the server without
/// knowing its address up-front.
///
/// Wire format:
///   request : "NOHDDX_DISCOVER"
///   reply   : "NOHDDX_SERVER:&lt;ip&gt;:&lt;port&gt;"
/// </summary>
public class DiscoveryResponderService : BackgroundService
{
    private readonly NohddxOptions _options;
    private readonly ILogger<DiscoveryResponderService> _logger;

    public DiscoveryResponderService(
        IOptions<NohddxOptions> options,
        ILogger<DiscoveryResponderService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Discovery.Enabled)
        {
            _logger.LogInformation("Discovery responder disabled in configuration.");
            return;
        }

        var port = _options.Discovery.Port;
        UdpClient? listener = null;

        try
        {
            listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            listener.EnableBroadcast = true;

            _logger.LogInformation("Discovery responder listening on UDP port {Port}", port);

            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await listener.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { break; }

                var payload = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (!payload.StartsWith("NOHDDX_DISCOVER", StringComparison.Ordinal))
                {
                    _logger.LogDebug("Ignoring non-discovery packet from {Remote}: {Payload}", result.RemoteEndPoint, payload);
                    continue;
                }

                var serverIp = ResolveResponseIp(result.RemoteEndPoint.Address);
                var serverPort = _options.Discovery.AnnouncedPort;
                var reply = Encoding.UTF8.GetBytes($"NOHDDX_SERVER:{serverIp}:{serverPort}");

                try
                {
                    await listener.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                    _logger.LogInformation(
                        "Discovery reply sent to {Remote}: server={Ip}:{Port}",
                        result.RemoteEndPoint, serverIp, serverPort);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send discovery reply to {Remote}", result.RemoteEndPoint);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery responder failed on port {Port}", port);
        }
        finally
        {
            listener?.Dispose();
            _logger.LogInformation("Discovery responder stopped.");
        }
    }

    /// <summary>
    /// Picks the local IP address that's most likely reachable from the
    /// requesting client. Honours an explicit override from configuration
    /// when set, otherwise picks the local address on the same subnet as
    /// the request, falling back to the first non-loopback IPv4.
    /// </summary>
    private string ResolveResponseIp(IPAddress remote)
    {
        if (!string.IsNullOrWhiteSpace(_options.Discovery.AnnouncedIp))
            return _options.Discovery.AnnouncedIp;

        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 1);
            if (probe.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch
        {
            // Fall through to interface enumeration.
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    return ua.Address.ToString();
            }
        }

        return "127.0.0.1";
    }
}
