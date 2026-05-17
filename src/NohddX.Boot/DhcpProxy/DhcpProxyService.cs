using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;

namespace NohddX.Boot.DhcpProxy;

/// <summary>
/// DHCP Proxy service that listens for PXE boot requests and responds with
/// appropriate boot file information (BIOS vs UEFI iPXE binaries).
/// Runs as a <see cref="BackgroundService"/> and implements <see cref="IDhcpProxyService"/>.
/// </summary>
public class DhcpProxyService : BackgroundService, IDhcpProxyService
{
    private UdpClient? _listener;
    private readonly NohddxOptions _options;
    private readonly ILogger<DhcpProxyService> _logger;

    // DHCP message types
    private const byte MessageTypeDiscover = 1;
    private const byte MessageTypeOffer = 2;
    private const byte MessageTypeRequest = 3;
    private const byte MessageTypeAck = 5;

    // DHCP option codes
    private const byte OptionMessageType = 53;
    private const byte OptionServerIdentifier = 54;
    private const byte OptionVendorClassIdentifier = 60;
    private const byte OptionEnd = 255;

    // Client architecture thresholds: values >= 6 indicate UEFI
    private const ushort UefiArchitectureThreshold = 6;

    public bool IsRunning { get; private set; }

    public DhcpProxyService(
        IOptions<NohddxOptions> options,
        ILogger<DhcpProxyService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DhcpProxy.Enabled)
        {
            _logger.LogInformation("DHCP Proxy is disabled in configuration");
            return;
        }

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _options.DhcpProxy.Port));
            _listener.EnableBroadcast = true;
            IsRunning = true;

            _logger.LogInformation(
                "DHCP Proxy started on port {Port}, BIOS={BiosFile}, UEFI={UefiFile}",
                _options.DhcpProxy.Port,
                _options.DhcpProxy.BiosBootFile,
                _options.DhcpProxy.UefiBootFile);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _listener.ReceiveAsync(stoppingToken);
                    await HandlePacketAsync(result.Buffer, result.RemoteEndPoint, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing DHCP packet");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start DHCP Proxy on port {Port}", _options.DhcpProxy.Port);
            throw;
        }
        finally
        {
            IsRunning = false;
            _listener?.Dispose();
            _listener = null;
            _logger.LogInformation("DHCP Proxy stopped");
        }
    }

    private async Task HandlePacketAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct)
    {
        DhcpPacket request;
        try
        {
            request = DhcpPacket.Parse(data);
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Ignoring malformed DHCP packet from {RemoteEndPoint}", remoteEp);
            return;
        }

        // Only respond to PXE clients (option 60 starts with "PXEClient")
        var vendorClass = request.GetVendorClassIdentifier();
        if (vendorClass == null || !vendorClass.StartsWith(Constants.DhcpOptions.PxeClientIdentifier, StringComparison.Ordinal))
            return;

        var messageType = request.GetMessageType();
        if (messageType != MessageTypeDiscover && messageType != MessageTypeRequest)
            return;

        // Determine BIOS vs UEFI from client architecture (option 93)
        var arch = request.GetClientArchitecture();
        var bootFile = (arch.HasValue && arch.Value >= UefiArchitectureThreshold)
            ? _options.DhcpProxy.UefiBootFile
            : _options.DhcpProxy.BiosBootFile;

        var mac = request.GetMacAddress();
        _logger.LogInformation(
            "PXE boot request from {Mac} (arch={Arch}, type={MessageType}), sending {BootFile}",
            mac, arch, messageType, bootFile);

        // Build response: OFFER for DISCOVER, ACK for REQUEST
        byte responseType = messageType == MessageTypeDiscover ? MessageTypeOffer : MessageTypeAck;
        var response = BuildResponse(request, bootFile, responseType);

        var responseBytes = response.ToBytes();

        try
        {
            await _listener!.SendAsync(responseBytes, responseBytes.Length,
                new IPEndPoint(IPAddress.Broadcast, 68));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DHCP response to {Mac}", mac);
        }
    }

    private DhcpPacket BuildResponse(DhcpPacket request, string bootFile, byte responseType)
    {
        var serverIp = ResolveServerIp();

        var response = new DhcpPacket
        {
            Op = 2, // BOOTREPLY
            HType = request.HType,
            HLen = request.HLen,
            Hops = 0,
            TransactionId = request.TransactionId,
            Seconds = 0,
            Flags = request.Flags,
            ClientIp = request.ClientIp,
            YourIp = IPAddress.Any,
            ServerIp = serverIp,
            GatewayIp = request.GatewayIp,
            BootFileName = bootFile,
        };

        // Copy client hardware address
        Array.Copy(request.ClientHardwareAddress, response.ClientHardwareAddress, request.ClientHardwareAddress.Length);

        // Set DHCP options
        response.Options[OptionMessageType] = new[] { responseType };
        response.Options[OptionServerIdentifier] = serverIp.GetAddressBytes();
        response.Options[OptionVendorClassIdentifier] = System.Text.Encoding.ASCII.GetBytes(Constants.DhcpOptions.PxeClientIdentifier);

        return response;
    }

    private IPAddress ResolveServerIp()
    {
        if (!string.IsNullOrEmpty(_options.DhcpProxy.NextServerIp) &&
            IPAddress.TryParse(_options.DhcpProxy.NextServerIp, out var configured))
        {
            return configured;
        }

        return GetLocalIpAddress();
    }

    private static IPAddress GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Connect to a public address to determine local IP (no actual traffic is sent)
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address;
        }
        catch
        {
            // Fallback: enumerate network interfaces
        }

        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                return ip;
        }

        return IPAddress.Loopback;
    }

    // Explicit interface implementation to expose Start/Stop outside BackgroundService lifecycle
    Task IDhcpProxyService.StartAsync(CancellationToken ct) => StartAsync(ct);
    Task IDhcpProxyService.StopAsync(CancellationToken ct) => StopAsync(ct);
}
